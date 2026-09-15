using EstudaBot.Domain.Chatbot;
using EstudaBot.Infrastructure.Knowledge;
using EstudaBot.Infrastructure.MachineLearning;

namespace EstudaBot.Application.Chatbot;

public sealed class StudyChatbot(IntentClassifier classifier, KnowledgeSearchService knowledgeSearch)
{
    public async Task<ChatbotReply> ReplyAsync(
        string message,
        ConversationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        message = message?.Trim() ?? string.Empty;
        var originalMessage = message;
        if (message.Length > TextNormalizer.MaxMessageLength)
        {
            return new ChatbotReply(
                $"Sua pergunta é muito longa. Use no máximo {TextNormalizer.MaxMessageLength} caracteres.",
                "entrada inválida", 1f);
        }

        var contextualMessage = AddConversationContext(message, context);
        if (contextualMessage is null && IsIncompleteQuestion(message))
        {
            return new ChatbotReply(
                "Sobre qual assunto você quer saber? Por exemplo: machine learning, fotossíntese ou gravidade.",
                "esclarecimento", 1f);
        }

        var hasConversationContext = contextualMessage is not null;
        message = contextualMessage ?? message;

        if (hasConversationContext && IsAcademicFollowUp(originalMessage))
        {
            return AcademicFollowUpReply(originalMessage, context?.Topic!);
        }

        var directTopic = ExtractDirectTopic(message);
        if (directTopic is not null)
        {
            var knowledge = await knowledgeSearch.SearchAsync(directTopic, cancellationToken);
            if (knowledge is not null)
            {
                return WebReply(knowledge, 1f);
            }
        }

        if (hasConversationContext && !string.IsNullOrWhiteSpace(context?.Topic))
        {
            var knowledge = await knowledgeSearch.SearchAsync(context.Topic, cancellationToken);
            if (knowledge is not null)
            {
                return WebReply(knowledge, 1f);
            }
        }

        var prediction = classifier.Predict(message);
        var confidence = prediction.Score.Length == 0 ? 0 : prediction.Score.Max();
        if (hasConversationContext || ShouldSearchWeb(message, prediction.Intent, confidence))
        {
            var knowledge = await knowledgeSearch.SearchAsync(message, cancellationToken);
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
        var normalized = TextNormalizer.CanonicalizeQuestion(message).TrimEnd('?', '!')
            .Replace("oque ", "o que ", StringComparison.Ordinal);
        var prefix = new[] { "o que e ", "o que sao ", "explique " }
            .FirstOrDefault(item => normalized.StartsWith(item, StringComparison.Ordinal));
        if (prefix is null) return null;

        var topic = normalized[prefix.Length..];
        var howItWorks = topic.IndexOf(" e como funciona", StringComparison.Ordinal);
        if (howItWorks >= 0) topic = topic[..howItWorks];
        return topic.Trim() is { Length: > 1 } result ? result : null;
    }

    public static string? AddConversationContext(string message, ConversationContext? context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.Topic) || !LooksLikeFollowUp(message))
        {
            return null;
        }

        var normalized = TextNormalizer.CanonicalizeQuestion(message);
        var separator = normalized.EndsWith("sobre", StringComparison.Ordinal) ? " " : " sobre ";
        return $"{message.TrimEnd('?', '!', '.', ' ')}{separator}{context.Topic}";
    }

    private static bool LooksLikeFollowUp(string message)
    {
        var normalized = TextNormalizer.CanonicalizeQuestion(message);
        var followUpMarkers = new[]
        {
            "sobre ", "isso", "esse assunto", "essa materia", "esse tema", "ele ", "ela ",
            "mais sobre", "a respeito", "como posso", "como devo", "como estudar",
            "qual faculdade", "qual curso", "o que preciso cursar", "por onde comeco",
            "quantos anos", "quanto tempo", "duracao", "duração", "qual salario", "qual salário",
            "quanto ganha", "mercado de trabalho", "onde trabalhar", "quais materias", "quais matérias"
        };

        return followUpMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)) ||
            normalized.EndsWith("sobre", StringComparison.Ordinal);
    }

    private static bool IsAcademicFollowUp(string message)
    {
        var normalized = TextNormalizer.CanonicalizeQuestion(message);
        return new[]
        {
            "qual faculdade", "qual curso", "o que preciso cursar", "por onde comeco",
            "quantos anos", "quanto tempo", "duracao", "qual salario", "quanto ganha",
            "mercado de trabalho", "onde trabalhar", "quais materias"
        }.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static ChatbotReply AcademicFollowUpReply(string message, string topic)
    {
        var normalized = TextNormalizer.CanonicalizeQuestion(message);
        var course = ExtractCourse(message) ?? topic;

        if (normalized.Contains("quantos anos", StringComparison.Ordinal) ||
            normalized.Contains("quanto tempo", StringComparison.Ordinal) ||
            normalized.Contains("duracao", StringComparison.Ordinal))
        {
            var duration = course.Contains("analise e desenvolvimento de sistemas", StringComparison.Ordinal)
                ? "geralmente 2 a 3 anos, porque esse curso costuma ser tecnólogo"
                : "normalmente entre 2 e 5 anos, dependendo do curso e da instituição";
            return new ChatbotReply(
                $"O curso de {course} {duration}. Confira a grade e a duração oficial da instituição, porque podem variar.",
                "informacao_academica", 1f);
        }

        if (normalized.Contains("qual faculdade", StringComparison.Ordinal) ||
            normalized.Contains("qual curso", StringComparison.Ordinal) ||
            normalized.Contains("o que preciso cursar", StringComparison.Ordinal))
        {
            return new ChatbotReply(
                $"Para seguir na área de {topic}, os cursos mais relacionados são Ciência da Computação, Sistemas de Informação, Engenharia de Software e Análise e Desenvolvimento de Sistemas. A melhor escolha depende do seu objetivo, da duração desejada e da grade da instituição.",
                "informacao_academica", 1f);
        }

        return new ChatbotReply(
            $"Para seguir em {course}, estude lógica de programação, uma linguagem como C# ou JavaScript, bancos de dados, HTTP, REST, autenticação e desenvolvimento de projetos práticos.",
            "informacao_academica", 1f);
    }

    private static string? ExtractCourse(string message)
    {
        var normalized = TextNormalizer.CanonicalizeQuestion(message);
        var courses = new[]
        {
            "analise e desenvolvimento de sistemas",
            "ciencia da computacao",
            "sistemas de informacao",
            "engenharia de software"
        };

        return courses.FirstOrDefault(course => normalized.Contains(course, StringComparison.Ordinal));
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
        var compact = new string(TextNormalizer.Normalize(message)
            .Where(character => !char.IsWhiteSpace(character) && character != '?' && character != '!')
            .ToArray());
        return compact.Length <= 40 &&
            ((compact.StartsWith("oque", StringComparison.Ordinal) && compact.Contains("funciona", StringComparison.Ordinal)) ||
             compact is "oquee" or "explique" or "comofunciona");
    }

}

public static class TextNormalizer
{
    public const int MaxMessageLength = 1000;

    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder();
        foreach (var character in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Trim();
    }

    public static string CanonicalizeQuestion(string value) =>
        Normalize(value).Replace("oque", "o que", StringComparison.Ordinal);
}
