using EstudaBot.Domain.Chatbot;
using Microsoft.ML;

namespace EstudaBot.Infrastructure.MachineLearning;

public sealed class IntentClassifier
{
    private readonly PredictionEngine<TrainingExample, IntentPrediction> _predictionEngine;

    public IntentClassifier()
    {
        var mlContext = new MLContext(seed: 42);
        var examples = BuildTrainingExamples();
        var trainingData = mlContext.Data.LoadFromEnumerable(examples);
        var pipeline = mlContext.Transforms.Conversion.MapValueToKey(
                outputColumnName: "Label",
                inputColumnName: nameof(TrainingExample.Intent))
            .Append(mlContext.Transforms.Text.FeaturizeText(
                outputColumnName: "Features",
                inputColumnName: nameof(TrainingExample.Text)))
            .Append(mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: "Label",
                featureColumnName: "Features"))
            .Append(mlContext.Transforms.Conversion.MapKeyToValue(
                outputColumnName: nameof(IntentPrediction.Intent),
                inputColumnName: "PredictedLabel"));

        var model = pipeline.Fit(trainingData);
        _predictionEngine = mlContext.Model.CreatePredictionEngine<TrainingExample, IntentPrediction>(model);
    }

    public IntentPrediction Predict(string message) =>
        _predictionEngine.Predict(new TrainingExample { Text = message });

    public static IReadOnlyDictionary<string, string[]> Responses { get; } = new Dictionary<string, string[]>
    {
        ["saudacao"] = ["Olá! Posso ajudar você a organizar seus estudos. Sobre qual assunto quer conversar?"],
        ["plano_estudos"] = [
            "Comece definindo o objetivo, divida o conteúdo em blocos de 25 minutos e reserve pausas de 5 minutos.",
            "Uma boa rotina alterna teoria, exercícios e revisão. Qual é a matéria e quando será sua prova?"
        ],
        ["machine_learning"] = [
            "Machine learning é uma área da IA em que modelos aprendem padrões a partir de dados para fazer previsões ou classificações.",
            "Na aprendizagem supervisionada, o modelo recebe exemplos com respostas conhecidas e aprende a prever respostas para novos dados."
        ],
        ["revisao"] = [
            "Use repetição espaçada: revise hoje, depois em alguns dias e novamente na semana seguinte.",
            "Tente explicar o conteúdo com suas próprias palavras e resolva questões sem consultar o material."
        ],
        ["motivacao"] = [
            "Comece com apenas 10 minutos. Uma tarefa pequena reduz a resistência e ajuda a criar ritmo.",
            "Escolha uma única tarefa para agora, deixe o celular longe e faça uma pausa curta ao concluir."
        ],
        ["despedida"] = ["Bons estudos! Até a próxima.", "Até mais! Volte quando quiser continuar estudando."]
    };

    private static TrainingExample[] BuildTrainingExamples() =>
    [
        new("oi", "saudacao"), new("olá", "saudacao"), new("bom dia", "saudacao"),
        new("pode me ajudar", "saudacao"), new("tudo bem", "saudacao"),
        new("monte um plano de estudos", "plano_estudos"), new("quero organizar meus estudos", "plano_estudos"),
        new("como criar uma rotina de estudos", "plano_estudos"), new("preciso estudar para uma prova", "plano_estudos"),
        new("me ajude a montar um cronograma", "plano_estudos"), new("qual a melhor forma de estudar", "plano_estudos"),
        new("o que é machine learning", "machine_learning"), new("explique aprendizado de máquina", "machine_learning"),
        new("como funciona aprendizado supervisionado", "machine_learning"), new("qual a diferença entre ia e machine learning", "machine_learning"),
        new("me explique modelos de classificação", "machine_learning"), new("quero aprender inteligência artificial", "machine_learning"),
        new("como revisar uma matéria", "revisao"), new("me ajude a revisar", "revisao"),
        new("quero fazer uma revisão", "revisao"), new("quando devo revisar o conteúdo", "revisao"),
        new("explique repetição espaçada", "revisao"), new("como memorizar melhor", "revisao"),
        new("estou desmotivado", "motivacao"), new("não consigo estudar", "motivacao"),
        new("estou procrastinando", "motivacao"), new("como manter o foco", "motivacao"),
        new("estudar está difícil", "motivacao"), new("preciso de motivação", "motivacao"),
        new("tchau", "despedida"), new("até mais", "despedida"), new("até logo", "despedida"),
        new("vou estudar", "despedida"), new("obrigado até depois", "despedida")
    ];
}
