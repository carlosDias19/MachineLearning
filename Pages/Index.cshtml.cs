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
    private const string ChatSessionVersion = "3";
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
        Messages.Add(new("user", message));

        if (message.Length > 0)
        {
            var directIncompleteCheck = message.Length <= 45 &&
                message.Contains("o que", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("funciona", StringComparison.OrdinalIgnoreCase) &&
                Tokenize(message).Count == 0;
            var reply = directIncompleteCheck
                ? new ChatbotReply("Sobre qual assunto você quer saber? Por exemplo: machine learning, fotossíntese ou gravidade.", "esclarecimento", 1f)
                : IsIncompleteQuestion(message)
                    ? await _chatbot.ReplyAsync(message)
                    : await FindApprovedAnswerAsync(message) ?? await _chatbot.ReplyAsync(message);
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
        }

        SaveMessages();
        Message = string.Empty;
        ModelState.Remove(nameof(Message));
    }

    public async Task<IActionResult> OnPostFeedbackAsync(int id, bool correct)
    {
        var interaction = await _db.Interactions.FindAsync(id);
        if (interaction is null)
        {
            return NotFound();
        }

        interaction.IsCorrect = correct;
        interaction.ApprovedForTraining = correct;
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
            .Where(interaction => interaction.ApprovedForTraining)
            .OrderByDescending(interaction => interaction.CreatedAtUtc)
            .Take(200)
            .ToListAsync();

        var bestMatch = approvedInteractions
            .Select(interaction => new
            {
                Interaction = interaction,
                Score = DiceSimilarity(questionTokens, Tokenize(interaction.Question))
            })
            .Where(match => match.Score >= 0.55 &&
                (!IsContentQuestion(question) || HasSources(match.Interaction.SourcesJson)))
            .OrderByDescending(match => match.Score)
            .FirstOrDefault();

        if (bestMatch is null)
        {
            return null;
        }

        var sources = JsonSerializer.Deserialize<List<KnowledgeResult>>(bestMatch.Interaction.SourcesJson) ?? [];
        return new ChatbotReply(
            bestMatch.Interaction.Answer,
            "resposta aprovada do banco",
            1f,
            null,
            sources);
    }

    private static bool IsContentQuestion(string question)
    {
        var normalized = RemoveAccents(question.ToLowerInvariant())
            .Replace("oque", "o que");
        return new[] { "o que e", "explique", "quem foi", "como funciona", "qual a diferenca", "sobre " }
            .Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsIncompleteQuestion(string question)
    {
        var compact = new string(question
            .ToLowerInvariant()
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
        var normalized = RemoveAccents(text.ToLowerInvariant()).Replace("oque", "o que");
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
        var sharedTopic = first.Any(token => token.Length >= 6 && second.Any(candidate => AreSimilarTokens(token, candidate)));
        return sharedTopic ? Math.Max(score, 0.6) : score;
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

    private static string RemoveAccents(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

public record ChatMessage(
    string Role,
    string Text,
    string? Intent = null,
    float? Confidence = null,
    IReadOnlyList<KnowledgeResult>? Sources = null,
    int? InteractionId = null);
