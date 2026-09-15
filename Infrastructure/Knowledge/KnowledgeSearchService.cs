using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EstudaBot.Domain.Chatbot;

namespace EstudaBot.Infrastructure.Knowledge;

public sealed class KnowledgeSearchService(HttpClient httpClient, ILogger<KnowledgeSearchService> logger)
{
    public async Task<KnowledgeBundle?> SearchAsync(string question, CancellationToken cancellationToken = default)
    {
        var searches = new List<Task<KnowledgeResult?>>
        {
            SearchWikipediaAsync(question, cancellationToken),
            SearchOpenAlexAsync(question, cancellationToken),
            SearchArxivAsync(question, cancellationToken)
        };

        if (IsBookQuestion(question))
        {
            searches.Add(SearchOpenLibraryAsync(question, cancellationToken));
        }

        var results = (await Task.WhenAll(searches))
            .Where(result => result is not null)
            .Cast<KnowledgeResult>()
            .ToList();
        if (results.Count == 0) return null;

        var primary = IsBookQuestion(question)
            ? results.FirstOrDefault(result => result.Provider == "Open Library")
            : IsScientificQuestion(question)
                ? results.FirstOrDefault(result => result.Provider == "OpenAlex")
                    ?? results.FirstOrDefault(result => result.Provider == "arXiv")
                    ?? results.First()
                : results.FirstOrDefault(result => result.Provider == "Wikipedia") ?? results.First();

        return new KnowledgeBundle(primary!, results);
    }

    private async Task<KnowledgeResult?> SearchWikipediaAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            var searchUrl = $"w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(question)}&format=json&utf8=1&srlimit=5";
            using var searchResponse = await GetWithRetryAsync(searchUrl, cancellationToken);
            if (!searchResponse.IsSuccessStatusCode) return null;

            await using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
            using var searchJson = await JsonDocument.ParseAsync(searchStream);
            var result = searchJson.RootElement.GetProperty("query").GetProperty("search");
            if (result.GetArrayLength() == 0) return null;

            var title = result.EnumerateArray()
                .Select(item => item.GetProperty("title").GetString())
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && IsRelevantTitle(question, candidate));
            if (string.IsNullOrWhiteSpace(title)) return null;

            var articlePath = title.Replace(' ', '_');
            using var articleResponse = await GetWithRetryAsync($"api/rest_v1/page/summary/{Uri.EscapeDataString(articlePath)}", cancellationToken);
            if (!articleResponse.IsSuccessStatusCode) return null;

            await using var articleStream = await articleResponse.Content.ReadAsStreamAsync();
            using var articleJson = await JsonDocument.ParseAsync(articleStream);
            var root = articleJson.RootElement;
            var extract = root.GetProperty("extract").GetString();
            var url = root.GetProperty("content_urls").GetProperty("desktop").GetProperty("page").GetString();
            return string.IsNullOrWhiteSpace(extract) || string.IsNullOrWhiteSpace(url)
                ? null
                : new KnowledgeResult("Wikipedia", title, extract.Trim(), url);
        }
        catch (HttpRequestException exception) { logger.LogWarning(exception, "Wikipedia search failed for {Question}", question); return null; }
        catch (TaskCanceledException exception) { logger.LogWarning(exception, "Wikipedia search timed out for {Question}", question); return null; }
        catch (JsonException exception) { logger.LogWarning(exception, "Wikipedia returned invalid JSON for {Question}", question); return null; }
    }

    private async Task<KnowledgeResult?> SearchOpenAlexAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.openalex.org/works?search={Uri.EscapeDataString(question)}&per-page=5";
            using var response = await GetWithRetryAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var results = json.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            var result = results.EnumerateArray()
                .FirstOrDefault(item => IsRelevantTitle(question, item.GetProperty("title").GetString()));
            if (result.ValueKind == JsonValueKind.Undefined) return null;

            var title = result.GetProperty("title").GetString();
            var id = result.GetProperty("id").GetString();
            var abstractText = ReadOpenAlexAbstract(result);
            return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(abstractText)
                ? null
                : new KnowledgeResult("OpenAlex", title, abstractText.Trim(), id);
        }
        catch (HttpRequestException exception) { logger.LogWarning(exception, "OpenAlex search failed for {Question}", question); return null; }
        catch (TaskCanceledException exception) { logger.LogWarning(exception, "OpenAlex search timed out for {Question}", question); return null; }
        catch (JsonException exception) { logger.LogWarning(exception, "OpenAlex returned invalid JSON for {Question}", question); return null; }
    }

    private async Task<KnowledgeResult?> SearchArxivAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://export.arxiv.org/api/query?search_query=all:{Uri.EscapeDataString(question)}&start=0&max_results=5";
            using var response = await GetWithRetryAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var xml = await response.Content.ReadAsStringAsync();
            var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
            var entry = XDocument.Parse(xml).Root?.Elements(atom + "entry")
                .FirstOrDefault(item => IsRelevantTitle(question, item.Element(atom + "title")?.Value));
            var title = entry?.Element(atom + "title")?.Value.Trim();
            var summary = entry?.Element(atom + "summary")?.Value.Trim();
            var link = entry?.Elements(atom + "link")
                .FirstOrDefault(item => item.Attribute("rel")?.Value is null or "alternate")
                ?.Attribute("href")?.Value;
            return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(link)
                ? null
                : new KnowledgeResult("arXiv", title, summary, link);
        }
        catch (HttpRequestException exception) { logger.LogWarning(exception, "arXiv search failed for {Question}", question); return null; }
        catch (TaskCanceledException exception) { logger.LogWarning(exception, "arXiv search timed out for {Question}", question); return null; }
        catch (XmlException exception) { logger.LogWarning(exception, "arXiv returned invalid XML for {Question}", question); return null; }
    }

    private async Task<KnowledgeResult?> SearchOpenLibraryAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(question)}&limit=1";
            using var response = await GetWithRetryAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var docs = json.RootElement.GetProperty("docs");
            if (docs.GetArrayLength() == 0) return null;

            var book = docs[0];
            var title = book.GetProperty("title").GetString();
            var key = book.GetProperty("key").GetString();
            var year = book.TryGetProperty("first_publish_year", out var yearValue) ? yearValue.ToString() : "ano não informado";
            return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(key)
                ? null
                : new KnowledgeResult("Open Library", title, $"Livro encontrado na Open Library. Primeira publicação: {year}.", $"https://openlibrary.org{key}");
        }
        catch (HttpRequestException exception) { logger.LogWarning(exception, "Open Library search failed for {Question}", question); return null; }
        catch (TaskCanceledException exception) { logger.LogWarning(exception, "Open Library search timed out for {Question}", question); return null; }
        catch (JsonException exception) { logger.LogWarning(exception, "Open Library returned invalid JSON for {Question}", question); return null; }
    }

    private static string? ReadOpenAlexAbstract(JsonElement result)
    {
        if (!result.TryGetProperty("abstract_inverted_index", out var invertedIndex) || invertedIndex.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var words = new SortedDictionary<int, string>();
        foreach (var word in invertedIndex.EnumerateObject())
        {
            foreach (var position in word.Value.EnumerateArray())
            {
                words[position.GetInt32()] = word.Name;
            }
        }

        return words.Count == 0 ? null : string.Join(' ', words.Values);
    }

    private static bool IsRelevantTitle(string question, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var ignoredTerms = new HashSet<string>(StringComparer.Ordinal)
        {
            "como", "posso", "fazer", "para", "estudar", "sobre", "isso", "explica", "explique",
            "qual", "quais", "funciona", "funcionar", "onde", "quando", "quem", "foi", "uma", "um"
        };
        var terms = question.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', '?', '!', ':', ';', '-', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length >= 4 && !ignoredTerms.Contains(term));
        var normalizedTitle = title.ToLowerInvariant();
        return terms.Any(term => normalizedTitle.Contains(term, StringComparison.Ordinal));
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await httpClient.GetAsync(url, cancellationToken);
                if (attempt < maxAttempts && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
                {
                    logger.LogWarning("Knowledge provider returned {StatusCode} for {Url}; retry {Attempt}/{MaxAttempts}", response.StatusCode, url, attempt, maxAttempts);
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException exception) when (attempt < maxAttempts)
            {
                logger.LogWarning(exception, "Knowledge provider request failed for {Url}; retry {Attempt}/{MaxAttempts}", url, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }

        throw new HttpRequestException($"Unable to retrieve knowledge from {url}.");
    }

    private static bool IsBookQuestion(string question) =>
        new[] { "livro", "livros", "autor", "autora", "bibliografia", "leitura" }
            .Any(word => question.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsScientificQuestion(string question) =>
        new[] { "artigo", "pesquisa", "científico", "cientifica", "estudo", "paper", "algoritmo", "teoria", "física", "biologia" }
            .Any(word => question.Contains(word, StringComparison.OrdinalIgnoreCase));
}
