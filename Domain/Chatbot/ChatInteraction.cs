namespace EstudaBot.Domain.Chatbot;

public sealed class ChatInteraction
{
    public int Id { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string Intent { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public string SourcesJson { get; set; } = "[]";
    public bool? IsCorrect { get; set; }
    public bool ApprovedForTraining { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
}
