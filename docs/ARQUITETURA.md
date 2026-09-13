# Arquitetura do EstudaBot

O EstudaBot é uma aplicação web em ASP.NET Core com Razor Pages, C#, ML.NET, SQLite e integrações com APIs públicas de conhecimento.

## Visão geral

```text
Usuário
  |
  v
Pages/                         Interface e entrada HTTP
  |
  v
Application/Chatbot/           Orquestra o caso de uso
  |--------------------|
  v                    v
Data/ + Domain/         Infrastructure/MachineLearning/
Banco SQLite            Classificação de intenções
                       |
                       v
                Infrastructure/Knowledge/
                Wikipedia, OpenAlex, arXiv, Open Library
```

## Camadas

### `Pages/`

É a camada de apresentação. As Razor Pages renderizam a interface do chat e recebem as ações HTTP:

- envio de perguntas;
- avaliação positiva ou negativa;
- início de uma nova conversa.

A página mantém o histórico visual na sessão do usuário e não contém a lógica de consulta às APIs nem o treinamento do modelo.

### `Application/Chatbot/`

Contém o caso de uso principal da aplicação em `StudyChatbot`.

Responsabilidades:

1. identificar perguntas incompletas;
2. extrair assuntos de perguntas como `o que é API?`;
3. decidir quando consultar a web;
4. chamar o classificador de intenções;
5. consolidar a resposta principal e um complemento;
6. devolver um `ChatbotReply` para a camada web.

Essa camada coordena o fluxo, mas não conhece detalhes de HTTP, Entity Framework ou dos formatos JSON/XML das APIs.

### `Domain/Chatbot/`

Contém os objetos centrais do domínio:

- `ChatbotReply`: resposta devolvida pelo chatbot;
- `KnowledgeResult`: resultado de uma fonte externa;
- `KnowledgeBundle`: conjunto de fontes e fonte principal;
- `ChatInteraction`: pergunta, resposta, avaliação e metadados persistidos;
- `TrainingExample` e `IntentPrediction`: contratos usados pelo classificador.

São modelos simples, sem dependência de Razor Pages ou de uma API específica.

### `Infrastructure/MachineLearning/`

Contém a implementação de ML.NET em `IntentClassifier`.

O classificador:

- transforma texto em características com `FeaturizeText`;
- treina um modelo multiclasses com regressão SDCA Maximum Entropy;
- reconhece intenções como saudação, plano de estudos, revisão, motivação e machine learning;
- fornece respostas locais para intenções operacionais.

A classificação é usada como apoio. Perguntas conceituais com assunto explícito priorizam as fontes externas para evitar respostas fixas fora de contexto.

### `Infrastructure/Knowledge/`

Contém `KnowledgeSearchService`, responsável pelas integrações externas:

- Wikipedia: conceitos gerais;
- OpenAlex: trabalhos científicos;
- arXiv: artigos de computação, matemática e ciência;
- Open Library: livros e bibliografia.

As consultas são executadas em paralelo. Uma API indisponível, lenta ou limitada não derruba o chatbot: o serviço ignora aquela fonte e usa as demais disponíveis.

### `Data/`

Contém `ChatDbContext`, o contexto do Entity Framework Core.

O SQLite armazena:

- pergunta original;
- resposta entregue;
- intenção e confiança;
- fontes consultadas;
- avaliação do usuário;
- aprovação para reutilização futura;
- datas de criação e revisão.

O arquivo `Data/estudabot.db` é local e não deve ser versionado.

## Fluxo de uma pergunta

1. A Razor Page recebe a mensagem.
2. O sistema procura uma resposta aprovada semelhante no SQLite.
3. Para perguntas conceituais, respostas aprovadas sem fonte não são reutilizadas.
4. Se necessário, `StudyChatbot` extrai o assunto e chama `KnowledgeSearchService`.
5. As fontes retornadas são consolidadas em uma resposta rastreável.
6. A pergunta, resposta e fontes são persistidas.
7. O usuário aprova ou reprova a resposta.
8. Respostas aprovadas ficam disponíveis para consultas semelhantes futuras.

## Dependências principais

- `Microsoft.ML`: classificação de intenções;
- `Microsoft.EntityFrameworkCore.Sqlite`: persistência local;
- ASP.NET Core Razor Pages: interface web e ciclo HTTP.

## Próximos passos naturais

- criar migrations do Entity Framework em vez de `EnsureCreated`;
- criar um painel de revisão das respostas aprovadas;
- treinar o classificador com exemplos aprovados após revisão;
- substituir o armazenamento em sessão por histórico associado a usuário;
- adicionar testes automatizados para roteamento, cache e provedores externos.
