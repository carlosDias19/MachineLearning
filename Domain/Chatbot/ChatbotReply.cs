namespace EstudaBot.Domain.Chatbot;

public sealed record ChatbotReply(
    string Text,
    string Intent,
    float Confidence,
    string? SourceTitle = null,
    IReadOnlyList<KnowledgeResult>? Sources = null);
