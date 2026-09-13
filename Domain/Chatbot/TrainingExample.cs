namespace EstudaBot.Domain.Chatbot;

public sealed class TrainingExample
{
    public TrainingExample() { }
    public TrainingExample(string text, string intent) => (Text, Intent) = (text, intent);
    public string Text { get; set; } = string.Empty;
    public string Intent { get; set; } = string.Empty;
}

public sealed class IntentPrediction
{
    public string Intent { get; set; } = string.Empty;
    public float[] Score { get; set; } = [];
}
