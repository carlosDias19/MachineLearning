# EstudaBot

Chatbot de estudos em ASP.NET Core, C# e ML.NET.

## Estrutura

```text
Application/Chatbot/       Orquestra o caso de uso do chatbot
Domain/Chatbot/            Entidades e contratos do domínio
Infrastructure/
  Knowledge/               Integrações Wikipedia, OpenAlex, arXiv e Open Library
  MachineLearning/         Classificador de intenções com ML.NET
Data/                      DbContext e banco SQLite local
Pages/                     Interface Razor Pages e handlers HTTP
wwwroot/                   CSS e arquivos estáticos
```

## Fluxo de uma pergunta

1. A página recebe a mensagem.
2. Respostas aprovadas são procuradas no banco por similaridade.
3. Perguntas conceituais consultam as fontes externas quando necessário.
4. O serviço de aplicação consolida a resposta.
5. A interação é salva no SQLite.
6. O usuário avalia a resposta.
7. Respostas aprovadas ficam disponíveis para reutilização.

## Executar

```powershell
dotnet restore
dotnet run
```

O banco local é criado em `Data/estudabot.db` na primeira execução.
