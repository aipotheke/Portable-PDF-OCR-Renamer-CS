using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PdfOcrRenamer;

public static class Ocr
{
    private static readonly HttpClient Shared = new();

    private static HttpClient MakeClient(Dictionary<string, object?> cfg)
    {
        var apiKey = AppConfig.GetApiKey(cfg);
        if (apiKey.Length == 0)
            throw new InvalidOperationException("No IONOS API key: set ionos_api_key in config.json or the IONOS_API_TOKEN env var.");
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.GetDouble("request_timeout", 120)) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static async Task<string> RetryCallAsync(Func<Task<string>> fn, int maxRetries, ILogger log)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try { return await fn(); }
            catch (Exception exc)
            {
                last = exc;
                if (!IsRetriable(exc) || attempt == maxRetries) throw;
                var wait = Math.Min(1 << attempt, 30);
                log.Warn($"OCR request failed (attempt {attempt}/{maxRetries}): {exc.Message} — retrying in {wait}s");
                await Task.Delay(TimeSpan.FromSeconds(wait));
            }
        }
        throw last!;
    }

    private static bool IsRetriable(Exception exc) =>
        exc is HttpRequestException { StatusCode: System.Net.HttpStatusCode st } && (st == System.Net.HttpStatusCode.TooManyRequests || ((int)st >= 500 && (int)st < 600));

    public static string RenderPageToDataUri(string pdfPath, int pageIndex, double scale)
    {
        using var doc = Docnet.Core.DocLib.Instance.GetDocReader(File.ReadAllBytes(pdfPath), new Docnet.Core.Models.PageDimensions(scale));
        using var page = doc.GetPageReader(pageIndex);
        var raw = page.GetImage();
        var png = PngEncoder.EncodeBgraAsPng(raw, page.GetPageWidth(), page.GetPageHeight());
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    public static int PageCount(string pdfPath)
    {
        using var doc = Docnet.Core.DocLib.Instance.GetDocReader(File.ReadAllBytes(pdfPath), new Docnet.Core.Models.PageDimensions(1.0));
        return doc.GetPageCount();
    }

    public static async Task<string> OcrPageAsync(HttpClient client, string model, string dataUri, Dictionary<string, object?> cfg, ILogger log)
    {
        var payload = new
        {
            model,
            max_tokens = cfg.GetInt("ocr_max_tokens", 4096),
            temperature = cfg.GetDouble("ocr_temperature", 0.2),
            messages = new object[]
            {
                new { role = "user", content = new object[] { new { type = "image_url", image_url = new { url = dataUri } } } },
            },
        };
        return await RetryCallAsync(async () =>
        {
            var url = cfg.GetString("ionos_base_url", "https://openai.inference.de-txl.ionos.com/v1").TrimEnd('/') + "/chat/completions";
            using var resp = await client.PostAsJsonAsync(url, payload);
            resp.EnsureSuccessStatusCode();
            return await ExtractContentAsync(resp) ?? "";
        }, cfg.GetInt("max_retries", 4), log);
    }

    private static async Task<string?> ExtractContentAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
            ? (content.ValueKind == JsonValueKind.String ? content.GetString() : null)
            : null;
    }

    public static async Task<string> OcrPdfAsync(string pdfPath, Dictionary<string, object?> cfg, ILogger log)
    {
        using var client = MakeClient(cfg);
        var model = cfg.GetString("ocr_model", "lightonai/LightOnOCR-2-1B");
        var scale = cfg.GetDouble("render_scale", 2.0);
        var pages = new List<string>();
        var n = PageCount(pdfPath);
        for (var i = 0; i < n; i++)
        {
            var dataUri = RenderPageToDataUri(pdfPath, i, scale);
            var md = await OcrPageAsync(client, model, dataUri, cfg, log);
            pages.Add($"<!-- page {i + 1} -->\n\n{md}");
        }
        return string.Join("\n\n", pages);
    }

    public static async Task<(string DocType, string Sender)> ClassifyAsync(string markdown, List<string> docTypes, Dictionary<string, object?> cfg, ILogger log)
    {
        var types = docTypes.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (types.Count == 0) return ("unknown", "");
        using var client = MakeClient(cfg);
        var model = cfg.GetString("classify_model", "mistralai/Mistral-Small-24B-Instruct");
        var allowed = string.Join(", ", types);
        var trimmed = markdown.Length > 8000 ? markdown[..8000] : markdown;
        var prompt =
            "You are a document classifier. Read the OCR text and answer in EXACTLY this format,\n" +
            "with nothing before or after:\n" +
            "type: <type>\n" +
            "sender: <company>\n\n" +
            "<type> is exactly ONE document type from this list (lowercase, no punctuation):\n" +
            $"{allowed}\n" +
            "<company> is the name of the company that sent the letter/scan. Look at the " +
            "letterhead, logo caption, sender address, imprint or signature block. Reply with " +
            "the company name only (no legal forms like GmbH/Inc. suffixes removed or kept, " +
            "no addresses, no explanations). If no company is identifiable, reply with: unknown\n\n" +
            $"OCR text:\n{trimmed}";
        var payload = new
        {
            model,
            temperature = 0.0,
            max_tokens = 64,
            messages = new object[]
            {
                new { role = "system", content = "Reply in exactly two lines: 'type: <type>' and 'sender: <company>'. No other text." },
                new { role = "user", content = prompt },
            },
        };
        string answer;
        try
        {
            answer = await RetryCallAsync(async () =>
            {
                var url = cfg.GetString("ionos_base_url", "https://openai.inference.de-txl.ionos.com/v1").TrimEnd('/') + "/chat/completions";
                using var resp = await client.PostAsJsonAsync(url, payload);
                resp.EnsureSuccessStatusCode();
                return (await ExtractContentAsync(resp) ?? "").Trim();
            }, cfg.GetInt("max_retries", 4), log);
        }
        catch (Exception exc)
        {
            log.Warn($"Classification call failed: {exc.Message} — falling back to 'unknown'");
            return ("unknown", "");
        }
        log.Info($"LLM classify answer: {answer}");
        var (matched, sender) = ParseAnswer(answer, types);
        if (matched is null)
        {
            log.Warn($"Classification answer '{answer}' not in allowed types — fallback 'unknown'");
            matched = "unknown";
        }
        return (matched, sender);
    }

    public static (string? Matched, string Sender) ParseAnswer(string answer, List<string> docTypes)
    {
        string? typeAnswer = null;
        var sender = "";
        foreach (var line in (answer ?? "").Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var key = line[..idx].Trim().ToLowerInvariant();
            var value = line[(idx + 1)..].Trim();
            if (key == "type") typeAnswer = value;
            else if (key == "sender") sender = value;
        }
        var matched = MatchType(typeAnswer ?? "", docTypes);
        if (new[] { "", "unknown", "none", "n/a" }.Contains(sender.ToLowerInvariant())) sender = "";
        return (matched, sender);
    }

    public static string? MatchType(string answer, List<string> docTypes)
    {
        var normalized = (answer ?? "").ToLowerInvariant().Trim().Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']');
        return docTypes.FirstOrDefault(t => t.ToLowerInvariant() == normalized);
    }
}
