using System.Globalization;
using System.Text.Json;
using System.Text;
using EstudaBot.Application.Chatbot;
using EstudaBot.Data;
using EstudaBot.Domain.Chatbot;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EstudaBot.Pages;

public class IndexModel : PageModel
{
    private const string ChatSessionKey = "estudabot.chat";
    private const string ChatSessionVersionKey = "estudabot.chat.version";
    private const string ChatSessionVersion = "4";
    private const int MaxSessionMessages = 40;
    private static readonly HashSet<string> StopWords =
    ["a", "as", "o", "os", "e", "de", "da", "do", "das", "dos", "em", "no", "na", "nos", "nas", "um", "uma", "uns", "umas", "que", "é", "para", "por", "como", "funciona", "funcionar", "explique", "explicar", "qual", "quem", "foi"];

    private readonly StudyChatbot _chatbot;
    private readonly ChatDbContext _db;

    public IndexModel(StudyChatbot chatbot, ChatDbContext db)
    {
        _chatbot = chatbot;
        _db = db;
    }

    [BindProperty]
    public string Message { get; set; } = string.Empty;

    public List<ChatMessage> Messages { get; private set; } = [];

    public void OnGet()
    {
        LoadMessages();
    }

    public async Task OnPostSendAsync()
    {
        LoadMessages();
        var message = Message.Trim();

        if (message.Length == 0)
        {
            SaveMessages();
            ModelState.Remove(nameof(Message));
            return;
        }

        if (message.Length > TextNormalizer.MaxMessageLength)
        {
            Messages.Add(new("assistant", $"Sua pergunta é muito longa. Use no máximo {TextNormalizer.MaxMessageLength} caracteres."));
            SaveMessages();
            Message = string.Empty;
            ModelState.Remove(nameof(Message));
            return;
        }

        Messages.Add(new("user", message));

        var context = BuildConversationContext();
        var lookupQuestion = StudyChatbot.AddConversationContext(message, context) ?? message;
        var reply = IsIncompleteQuestion(message)
            ? await _chatbot.ReplyAsync(message, context)
            : await FindApprovedAnswerAsync(lookupQuestion) ?? await _chatbot.ReplyAsync(message, context);
        var interaction = new ChatInteraction
        {
            Question = message,
            Answer = reply.Text,
            Intent = reply.Intent,
            Confidence = reply.Confidence,
            SourcesJson = JsonSerializer.Serialize(reply.Sources ?? []),
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.Interactions.Add(interaction);
        await _db.SaveChangesAsync();

        Messages.Add(new("assistant", reply.Text, reply.Intent, reply.Confidence, reply.Sources, interaction.Id));

        SaveMessages();
        Message = string.Empty;
        ModelState.Remove(nameof(Message));
    }

    public async Task<IActionResult> OnPostFeedbackAsync(int id, bool correct)
    {
        LoadMessages();
        if (!Messages.Any(message => message.InteractionId == id))
        {
            return Forbid();
        }

        var interaction = await _db.Interactions.FindAsync(id);
        if (interaction is null)
        {
            return NotFound();
        }

        interaction.IsCorrect = correct;
        interaction.ApprovedForTraining = correct && HasSources(interaction.SourcesJson);
        interaction.ReviewedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }

    public IActionResult OnPostNewChat()
    {
        HttpContext.Session.Remove(ChatSessionKey);
        HttpContext.Session.SetString(ChatSessionVersionKey, ChatSessionVersion);
        return RedirectToPage();
    }

    private void LoadMessages()
    {
        if (HttpContext.Session.GetString(ChatSessionVersionKey) != ChatSessionVersion)
        {
            HttpContext.Session.Remove(ChatSessionKey);
            HttpContext.Session.SetString(ChatSessionVersionKey, ChatSessionVersion);
        }

        var saved = HttpContext.Session.GetString(ChatSessionKey);
        Messages = string.IsNullOrWhiteSpace(saved)
            ? []
            : JsonSerializer.Deserialize<List<ChatMessage>>(saved) ?? [];

        if (Messages.Count == 0)
        {
            Messages.Add(new("assistant", "Olá! Sou o EstudaBot. Pergunte sobre rotina, revisão, motivação ou machine learning."));
        }
    }

    private void SaveMessages()
    {
        if (Messages.Count > MaxSessionMessages)
        {
            Messages = Messages.TakeLast(MaxSessionMessages).ToList();
        }

        HttpContext.Session.SetString(ChatSessionKey, JsonSerializer.Serialize(Messages));
    }

    private async Task<ChatbotReply?> FindApprovedAnswerAsync(string question)
    {
        var questionTokens = Tokenize(question);
        if (questionTokens.Count == 0)
        {
            return null;
        }

        var approvedInteractions = await _db.Interactions
            .AsNoTracking()
            .Where(interaction => interaction.ApprovedForTraining && interaction.SourcesJson != "[]")
            .OrderByDescending(interaction => interaction.CreatedAtUtc)
            .Take(200)
            .ToListAsync();

        var canonicalQuestion = TextNormalizer.CanonicalizeQuestion(question);
        var exactMatch = approvedInteractions.FirstOrDefault(interaction =>
            TextNormalizer.CanonicalizeQuestion(interaction.Question) == canonicalQuestion);
        if (exactMatch is not null)
        {
            return ToApprovedReply(exactMatch);
        }

        var bestMatch = approvedInteractions
            .Select(interaction => new
            {
                Interaction = interaction,
                Score = DiceSimilarity(questionTokens, Tokenize(interaction.Question))
            })
            .Where(match => match.Score >= 0.82 &&
                (!IsContentQuestion(question) || HasSources(match.Interaction.SourcesJson)))
            .OrderByDescending(match => match.Score)
            .FirstOrDefault();

        if (bestMatch is null)
        {
            return null;
        }

        return ToApprovedReply(bestMatch.Interaction);
    }

    private ConversationContext? BuildConversationContext()
    {
        for (var index = Messages.Count - 1; index >= 1; index--)
        {
            var assistant = Messages[index];
            if (assistant.Role != "assistant" || assistant.Sources is not { Count: > 0 })
            {
                continue;
            }

            var previousUser = Messages
                .Take(index)
                .LastOrDefault(message => message.Role == "user");
            var topic = assistant.Sources[0].Title;
            return new ConversationContext(topic, previousUser?.Text, assistant.Text);
        }

        return null;
    }

    private static ChatbotReply ToApprovedReply(ChatInteraction interaction)
    {
        var sources = JsonSerializer.Deserialize<List<KnowledgeResult>>(interaction.SourcesJson) ?? [];
        return new ChatbotReply(
            interaction.Answer,
            "resposta aprovada do banco",
            0.99f,
            null,
            sources);
    }

    private static bool IsContentQuestion(string question)
    {
        var normalized = TextNormalizer.CanonicalizeQuestion(question);
        return new[] { "o que e", "explique", "quem foi", "como funciona", "qual a diferenca", "sobre " }
            .Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsIncompleteQuestion(string question)
    {
        var compact = new string(TextNormalizer.Normalize(question)
            .Where(character => !char.IsWhiteSpace(character) && character != '?' && character != '!')
            .ToArray());
        return compact.Length <= 40 &&
            ((compact.StartsWith("oque", StringComparison.Ordinal) && compact.Contains("funciona", StringComparison.Ordinal)) ||
             compact is "oquee" or "explique" or "comofunciona");
    }

    private static bool HasSources(string sourcesJson)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<KnowledgeResult>>(sourcesJson) ?? []).Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HashSet<string> Tokenize(string text)
    {
        var normalized = TextNormalizer.CanonicalizeQuestion(text);
        var tokens = normalized
            .Split([' ', '\t', '\r', '\n', '.', ',', '?', '!', ':', ';', '-', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1 && !StopWords.Contains(token));
        return tokens.ToHashSet();
    }

    private static double DiceSimilarity(HashSet<string> first, HashSet<string> second)
    {
        if (first.Count == 0 || second.Count == 0)
        {
            return 0;
        }

        var unmatched = second.ToHashSet();
        var overlap = 0;
        foreach (var token in first)
        {
            var match = unmatched.FirstOrDefault(candidate => AreSimilarTokens(token, candidate));
            if (match is not null)
            {
                overlap++;
                unmatched.Remove(match);
            }
        }

        var score = 2d * overlap / (first.Count + second.Count);
        return score;
    }

    private static bool AreSimilarTokens(string first, string second)
    {
        return first == second || (first.Length >= 5 && second.Length >= 5 && EditDistance(first, second) <= 1);
    }

    private static int EditDistance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (var row = 1; row <= first.Length; row++)
        {
            var current = new int[second.Length + 1];
            current[0] = row;
            for (var column = 1; column <= second.Length; column++)
            {
                var cost = first[row - 1] == second[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            previous = current;
        }

        return previous[^1];
    }

}

public record ChatMessage(
    string Role,
    string Text,
    string? Intent = null,
    float? Confidence = null,
    IReadOnlyList<KnowledgeResult>? Sources = null,
    int? InteractionId = null);
