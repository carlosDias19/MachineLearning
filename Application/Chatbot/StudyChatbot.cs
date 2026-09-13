using System.Globalization;
using System.Text;
using EstudaBot.Domain.Chatbot;
using EstudaBot.Infrastructure.Knowledge;
using EstudaBot.Infrastructure.MachineLearning;

namespace EstudaBot.Application.Chatbot;

public sealed class StudyChatbot(IntentClassifier classifier, KnowledgeSearchService knowledgeSearch)
{
    public async Task<ChatbotReply> ReplyAsync(string message)
    {
        if (IsIncompleteQuestion(message))
        {
            return new ChatbotReply(
                "Sobre qual assunto você quer saber? Por exemplo: machine learning, fotossíntese ou gravidade.",
                "esclarecimento", 1f);
        }

        var directTopic = ExtractDirectTopic(message);
        if (directTopic is not null)
        {
            var knowledge = await knowledgeSearch.SearchAsync(directTopic);
            if (knowledge is not null)
            {
                return WebReply(knowledge, 1f);
            }
        }

        var prediction = classifier.Predict(message);
        var confidence = prediction.Score.Length == 0 ? 0 : prediction.Score.Max();
        if (ShouldSearchWeb(message, prediction.Intent, confidence))
        {
            var knowledge = await knowledgeSearch.SearchAsync(message);
            if (knowledge is not null)
            {
                return WebReply(knowledge, confidence);
            }
        }

        if (confidence < 0.35f || !IntentClassifier.Responses.TryGetValue(prediction.Intent, out var options))
        {
            return new ChatbotReply(
                "Não encontrei uma fonte confiável para essa pergunta agora. Tente reformular o assunto ou tente novamente.",
                "não identificada", confidence);
        }

        return new ChatbotReply(options[Random.Shared.Next(options.Length)], prediction.Intent, confidence);
    }

    private static ChatbotReply WebReply(KnowledgeBundle knowledge, float confidence) =>
        new(ComposeKnowledgeAnswer(knowledge), "pesquisa na web", confidence, knowledge.Primary.Title, knowledge.Sources);

    private static string ComposeKnowledgeAnswer(KnowledgeBundle knowledge)
    {
        var answer = $"Resposta baseada em {knowledge.Primary.Provider}:\n\n{knowledge.Primary.Extract.Trim()}";
        var complement = knowledge.Sources
            .Where(source => source.Provider != knowledge.Primary.Provider)
            .Select(source => new { Source = source, Sentence = FirstSentence(source.Extract) })
            .FirstOrDefault(item => item.Sentence.Length > 0);
        return complement is null
            ? answer
            : $"{answer}\n\nComplemento de {complement.Source.Provider}:\n{complement.Sentence}";
    }

    private static string FirstSentence(string text)
    {
        var sentence = text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.Length > 0);
        return sentence is null ? string.Empty : $"{sentence}.";
    }

    private static string? ExtractDirectTopic(string message)
    {
        var normalized = RemoveAccents(message.ToLowerInvariant()).Trim().TrimEnd('?', '!')
            .Replace("oque ", "o que ", StringComparison.Ordinal);
        var prefix = new[] { "o que e ", "o que sao ", "explique " }
            .FirstOrDefault(item => normalized.StartsWith(item, StringComparison.Ordinal));
        if (prefix is null) return null;

        var topic = normalized[prefix.Length..];
        var howItWorks = topic.IndexOf(" e como funciona", StringComparison.Ordinal);
        if (howItWorks >= 0) topic = topic[..howItWorks];
        return topic.Trim() is { Length: > 1 } result ? result : null;
    }

    private static bool ShouldSearchWeb(string message, string intent, float confidence)
    {
        if (intent is "saudacao" or "plano_estudos" or "revisao" or "motivacao" or "despedida")
        {
            var studyWords = new[] { "plano", "cronograma", "rotina", "revis", "memor", "procrast", "foco", "motiv" };
            if (studyWords.Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        }

        var markers = new[] { "o que é", "oque é", "explique", "quem foi", "como funciona", "qual a diferença", "sobre " };
        return confidence < 0.35f || markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIncompleteQuestion(string message)
    {
        var compact = new string(RemoveAccents(message.ToLowerInvariant())
            .Where(character => !char.IsWhiteSpace(character) && character != '?' && character != '!')
            .ToArray());
        return compact.Length <= 40 &&
            ((compact.StartsWith("oque", StringComparison.Ordinal) && compact.Contains("funciona", StringComparison.Ordinal)) ||
             compact is "oquee" or "explique" or "comofunciona");
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
