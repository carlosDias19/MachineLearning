namespace EstudaBot.Domain.Chatbot;

public sealed record KnowledgeResult(string Provider, string Title, string Extract, string Url);

public sealed record KnowledgeBundle(KnowledgeResult Primary, IReadOnlyList<KnowledgeResult> Sources);
