using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PdfOcrRenamer
{
    public static class Ocr
    {
        public static HttpClient MakeClient(Dictionary<string, object> cfg)
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
            Exception last = null;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try { return await fn(); }
                catch (Exception exc)
                {
                    last = exc;
                    if (!IsRetriable(exc) || attempt == maxRetries) throw;
                    var wait = Math.Min(1 << attempt, 30);
                    log.Warn("OCR request failed (attempt " + attempt + "/" + maxRetries + "): " + exc.Message + " — retrying in " + wait + "s");
                    await Task.Delay(TimeSpan.FromSeconds(wait));
                }
            }
            throw last;
        }

        private static bool IsRetriable(Exception exc)
        {
            var hre = exc as HttpRequestException;
            if (hre == null) return false;
            var msg = hre.Message ?? "";
            if (msg.StartsWith("HTTP "))
            {
                var sp = msg.IndexOf(' ');
                var rest = msg.Substring(sp + 1);
                var end = 0;
                while (end < rest.Length && rest[end] >= '0' && rest[end] <= '9') end++;
                int st;
                if (end > 0 && int.TryParse(rest.Substring(0, end), out st))
                    return st == 429 || (st >= 500 && st < 600);
            }
            return false;
        }

        public static string RenderPageToDataUri(string pdfPath, int pageIndex, double scale)
        {
            using (var doc = Docnet.Core.DocLib.Instance.GetDocReader(File.ReadAllBytes(pdfPath), new Docnet.Core.Models.PageDimensions(scale)))
            using (var page = doc.GetPageReader(pageIndex))
            {
                var raw = page.GetImage();
                var png = PngEncoder.EncodeBgraAsPng(raw, page.GetPageWidth(), page.GetPageHeight());
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }
        }

        public static int PageCount(string pdfPath)
        {
            using (var doc = Docnet.Core.DocLib.Instance.GetDocReader(File.ReadAllBytes(pdfPath), new Docnet.Core.Models.PageDimensions(1.0)))
            {
                return doc.GetPageCount();
            }
        }

        private static string Endpoint(Dictionary<string, object> cfg)
        {
            var baseUrl = cfg.GetString("ionos_base_url", "https://openai.inference.de-txl.ionos.com/v1");
            return baseUrl.TrimEnd('/') + "/chat/completions";
        }

        private static async Task<string> PostChatAsync(HttpClient client, string url, string jsonBody)
        {
            using (var content = new StringContent(jsonBody, Encoding.UTF8, "application/json"))
            using (var resp = await client.PostAsync(url, content))
            {
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException("HTTP " + (int)resp.StatusCode + ": " + body);
                using (var doc = JsonDocument.Parse(body))
                {
                    JsonElement contentEl;
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out contentEl)
                        && contentEl.ValueKind == JsonValueKind.String)
                    {
                        return contentEl.GetString();
                    }
                }
            }
            return null;
        }

        public static async Task<string> OcrPageAsync(HttpClient client, string model, string dataUri, Dictionary<string, object> cfg, ILogger log)
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = model,
                max_tokens = cfg.GetInt("ocr_max_tokens", 4096),
                temperature = cfg.GetDouble("ocr_temperature", 0.2),
                messages = new object[]
                {
                    new { role = "user", content = new object[] { new { type = "image_url", image_url = new { url = dataUri } } } },
                },
            });
            return await RetryCallAsync(async () =>
            {
                var result = await PostChatAsync(client, Endpoint(cfg), payload);
                return result ?? "";
            }, cfg.GetInt("max_retries", 4), log);
        }

        public static async Task<string> OcrPdfAsync(string pdfPath, Dictionary<string, object> cfg, ILogger log)
        {
            using (var client = MakeClient(cfg))
            {
                var model = cfg.GetString("ocr_model", "lightonai/LightOnOCR-2-1B");
                var scale = cfg.GetDouble("render_scale", 2.0);
                var pages = new List<string>();
                var n = PageCount(pdfPath);
                for (var i = 0; i < n; i++)
                {
                    var dataUri = RenderPageToDataUri(pdfPath, i, scale);
                    var md = await OcrPageAsync(client, model, dataUri, cfg, log);
                    pages.Add("<!-- page " + (i + 1) + " -->\n\n" + md);
                }
                return string.Join("\n\n", pages.ToArray());
            }
        }

        public static async Task<ClassifyResult> ClassifyAsync(string markdown, List<string> docTypes, Dictionary<string, object> cfg, ILogger log)
        {
            var types = docTypes.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (types.Count == 0) return new ClassifyResult { DocType = "unknown", Sender = "" };
            using (var client = MakeClient(cfg))
            {
                var model = cfg.GetString("classify_model", "mistralai/Mistral-Small-24B-Instruct");
                var allowed = string.Join(", ", types.ToArray());
                var trimmed = markdown.Length > 8000 ? markdown.Substring(0, 8000) : markdown;
                var prompt =
                    "You are a document classifier. Read the OCR text and answer in EXACTLY this format,\n" +
                    "with nothing before or after:\n" +
                    "type: <type>\n" +
                    "sender: <company>\n\n" +
                    "<type> is exactly ONE document type from this list (lowercase, no punctuation):\n" +
                    allowed + "\n" +
                    "<company> is the name of the company that sent the letter/scan. Look at the " +
                    "letterhead, logo caption, sender address, imprint or signature block. Reply with " +
                    "the company name only (no legal forms like GmbH/Inc. suffixes removed or kept, " +
                    "no addresses, no explanations). If no company is identifiable, reply with: unknown\n\n" +
                    "OCR text:\n" + trimmed;
                var payload = JsonSerializer.Serialize(new
                {
                    model = model,
                    temperature = 0.0,
                    max_tokens = 64,
                    messages = new object[]
                    {
                        new { role = "system", content = "Reply in exactly two lines: 'type: <type>' and 'sender: <company>'. No other text." },
                        new { role = "user", content = prompt },
                    },
                });
                string answer;
                try
                {
                    answer = await RetryCallAsync(async () =>
                    {
                        var result = await PostChatAsync(client, Endpoint(cfg), payload);
                        return (result ?? "").Trim();
                    }, cfg.GetInt("max_retries", 4), log);
                }
                catch (Exception exc)
                {
                    log.Warn("Classification call failed: " + exc.Message + " — falling back to 'unknown'");
                    return new ClassifyResult { DocType = "unknown", Sender = "" };
                }
                log.Info("LLM classify answer: " + answer);
                var parsed = ParseAnswer(answer, types);
                if (parsed.Matched == null)
                {
                    log.Warn("Classification answer '" + answer + "' not in allowed types — fallback 'unknown'");
                    parsed.Matched = "unknown";
                }
                return new ClassifyResult { DocType = parsed.Matched, Sender = parsed.Sender };
            }
        }

        public struct ClassifyResult
        {
            public string DocType;
            public string Sender;
        }

        public struct ParsedAnswer
        {
            public string Matched;
            public string Sender;
        }

        public static ParsedAnswer ParseAnswer(string answer, List<string> docTypes)
        {
            string typeAnswer = null;
            var sender = "";
            foreach (var line in (answer ?? "").Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var key = line.Substring(0, idx).Trim().ToLowerInvariant();
                var value = line.Substring(idx + 1).Trim();
                if (key == "type") typeAnswer = value;
                else if (key == "sender") sender = value;
            }
            var matched = MatchType(typeAnswer ?? "", docTypes);
            var lower = (sender ?? "").ToLowerInvariant();
            if (lower == "" || lower == "unknown" || lower == "none" || lower == "n/a") sender = "";
            var result = new ParsedAnswer();
            result.Matched = matched;
            result.Sender = sender;
            return result;
        }

        public static string MatchType(string answer, List<string> docTypes)
        {
            var normalized = (answer ?? "").ToLowerInvariant().Trim().Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']');
            foreach (var t in docTypes)
                if (t.ToLowerInvariant() == normalized) return t;
            return null;
        }
    }
}
