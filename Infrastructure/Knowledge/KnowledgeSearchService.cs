using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EstudaBot.Domain.Chatbot;

namespace EstudaBot.Infrastructure.Knowledge;

public sealed class KnowledgeSearchService(HttpClient httpClient)
{
    public async Task<KnowledgeBundle?> SearchAsync(string question)
    {
        var searches = new List<Task<KnowledgeResult?>>
        {
            SearchWikipediaAsync(question),
            SearchOpenAlexAsync(question),
            SearchArxivAsync(question)
        };

        if (IsBookQuestion(question))
        {
            searches.Add(SearchOpenLibraryAsync(question));
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

    private async Task<KnowledgeResult?> SearchWikipediaAsync(string question)
    {
        try
        {
            var searchUrl = $"w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(question)}&format=json&utf8=1&srlimit=1";
            using var searchResponse = await httpClient.GetAsync(searchUrl);
            if (!searchResponse.IsSuccessStatusCode) return null;

            await using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
            using var searchJson = await JsonDocument.ParseAsync(searchStream);
            var result = searchJson.RootElement.GetProperty("query").GetProperty("search");
            if (result.GetArrayLength() == 0) return null;

            var title = result[0].GetProperty("title").GetString();
            if (string.IsNullOrWhiteSpace(title)) return null;

            var articlePath = title.Replace(' ', '_');
            using var articleResponse = await httpClient.GetAsync($"api/rest_v1/page/summary/{Uri.EscapeDataString(articlePath)}");
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
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task<KnowledgeResult?> SearchOpenAlexAsync(string question)
    {
        try
        {
            var url = $"https://api.openalex.org/works?search={Uri.EscapeDataString(question)}&per-page=1";
            using var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var results = json.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            var result = results[0];
            var title = result.GetProperty("title").GetString();
            var id = result.GetProperty("id").GetString();
            var abstractText = ReadOpenAlexAbstract(result);
            return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(abstractText)
                ? null
                : new KnowledgeResult("OpenAlex", title, abstractText.Trim(), id);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task<KnowledgeResult?> SearchArxivAsync(string question)
    {
        try
        {
            var url = $"https://export.arxiv.org/api/query?search_query=all:{Uri.EscapeDataString(question)}&start=0&max_results=1";
            using var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var xml = await response.Content.ReadAsStringAsync();
            var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
            var entry = XDocument.Parse(xml).Root?.Element(atom + "entry");
            var title = entry?.Element(atom + "title")?.Value.Trim();
            var summary = entry?.Element(atom + "summary")?.Value.Trim();
            var link = entry?.Elements(atom + "link")
                .FirstOrDefault(item => item.Attribute("rel")?.Value is null or "alternate")
                ?.Attribute("href")?.Value;
            return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(link)
                ? null
                : new KnowledgeResult("arXiv", title, summary, link);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (XmlException) { return null; }
    }

    private async Task<KnowledgeResult?> SearchOpenLibraryAsync(string question)
    {
        try
        {
            var url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(question)}&limit=1";
            using var response = await httpClient.GetAsync(url);
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
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
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

    private static bool IsBookQuestion(string question) =>
        new[] { "livro", "livros", "autor", "autora", "bibliografia", "leitura" }
            .Any(word => question.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsScientificQuestion(string question) =>
        new[] { "artigo", "pesquisa", "científico", "cientifica", "estudo", "paper", "algoritmo", "teoria", "física", "biologia" }
            .Any(word => question.Contains(word, StringComparison.OrdinalIgnoreCase));
}
