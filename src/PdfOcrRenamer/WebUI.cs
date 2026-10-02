using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfOcrRenamer;

public sealed class WebUI : IDisposable
{
    public const int DefaultPort = 8765;

    public static readonly string[] EditableKeys =
    {
        "ionos_api_key", "watch_folder", "doc_types", "keep_md_sidecar", "sender_in_filename",
        "render_scale", "ocr_max_tokens", "ocr_temperature", "request_timeout", "max_retries",
        "stability_seconds", "stability_max_wait",
    };

    private readonly HttpListener _listener = new();
    private readonly ILogger _log;
    private Thread? _serverThread;

    public Dictionary<string, object?> Config { get; }
    public FolderWatcher Watcher { get; }
    public int Port { get; }

    public WebUI(Dictionary<string, object?>? config = null, FolderWatcher? watcher = null, int port = DefaultPort, ILogger? log = null)
    {
        Config = config ?? AppConfig.LoadConfig();
        Watcher = watcher ?? new FolderWatcher(Config);
        Port = port;
        _log = log ?? new ConsoleLogger();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public object Status() => new
    {
        watch_folder = Watcher.Folder(),
        paused = Watcher.Paused,
        has_api_key = AppConfig.GetApiKey(Config).Length > 0,
        doc_types = Config.GetList("doc_types"),
        jobs = Watcher.JobList(),
    };

    public void Start()
    {
        Watcher.Start();
        _listener.Start();
        _serverThread = new Thread(() =>
        {
            try
            {
                while (_listener.IsListening) _ = Task.Run(() => Handle(_listener.GetContext()));
            }
            catch (HttpListenerException) { }
        })
        { IsBackground = true, Name = "webui" };
        _serverThread.Start();
        _log.Info($"Web UI: http://127.0.0.1:{Port}");
    }

    public void Stop()
    {
        if (_listener.IsListening) _listener.Stop();
        Watcher.Stop();
    }

    public void Dispose() => Stop();

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.HttpMethod == "GET")
            {
                if (path is "/" or "/index.html") await ServeIndex(ctx);
                else if (path == "/api/status") await SendJson(ctx, Status());
                else if (path == "/api/config") await SendJson(ctx, MaskedConfig(Config));
                else await SendJson(ctx, new { error = "not found" }, 404);
            }
            else if (ctx.Request.HttpMethod == "POST")
            {
                if (path == "/api/config") await PostConfig(ctx);
                else if (path == "/api/scan") await PostScan(ctx);
                else await SendJson(ctx, new { error = "not found" }, 404);
            }
            else await SendJson(ctx, new { error = "not found" }, 404);
        }
        catch (Exception) { }
    }

    private static async Task ServeIndex(HttpListenerContext ctx)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "WebUI", "index.html");
        if (!File.Exists(path))
        {
            await SendJson(ctx, new { error = "index.html not found" }, 500);
            return;
        }
        var body = File.ReadAllBytes(path);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }

    private static async Task SendJson(HttpListenerContext ctx, object payload, int status = 200)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, ConfigJson.Options));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }

    private async Task PostConfig(HttpListenerContext ctx)
    {
        JsonNode? update;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            update = JsonNode.Parse(await reader.ReadToEndAsync());
        }
        catch
        {
            await SendJson(ctx, new { error = "invalid JSON body" }, 400);
            return;
        }
        if (update is not JsonObject)
        {
            await SendJson(ctx, new { error = "body must be a JSON object" }, 400);
            return;
        }
        var updateObj = (JsonObject)update;
        var error = ValidateConfigUpdate(updateObj);
        if (error is not null)
        {
            await SendJson(ctx, new { error }, 400);
            return;
        }
        ApplyConfigUpdate(updateObj);
        _log.Info($"Config updated via web UI: {string.Join(", ", updateObj.Select(kv => kv.Key))}");
        await SendJson(ctx, new { ok = true, config = MaskedConfig(Config) });
    }

    private static string? ValidateConfigUpdate(JsonObject update)
    {
        if (update.TryGetPropertyValue("watch_folder", out var wf))
        {
            var folder = (wf?.GetValue<string>() ?? "").Trim();
            if (folder.Length > 0 && !Directory.Exists(folder))
                return $"Watch folder does not exist: {folder}";
        }
        if (update.TryGetPropertyValue("doc_types", out var dt))
        {
            if (dt is not JsonArray arr) return "doc_types must be a list";
            var cleaned = arr.Select(n => (n?.GetValue<string>() ?? "").Trim()).Where(s => s.Length > 0).ToList();
            if (cleaned.Count == 0) return "At least one document type is required";
            update["doc_types"] = new JsonArray(cleaned.Select(s => JsonValue.Create(s)).ToArray());
        }
        if (update.TryGetPropertyValue("ionos_api_key", out var key) && (key?.GetValue<string>() ?? "").Trim().Length == 0)
            return "API key must not be empty";
        foreach (var numeric in new[] { "render_scale", "ocr_max_tokens", "ocr_temperature", "request_timeout", "max_retries", "stability_seconds", "stability_max_wait" })
        {
            if (update.TryGetPropertyValue(numeric, out var v))
            {
                try { _ = v?.GetValue<double>(); }
                catch { return $"{numeric} must be a number"; }
            }
        }
        var unknown = update.Select(kv => kv.Key).Where(k => !EditableKeys.Contains(k)).ToList();
        if (unknown.Count > 0) return $"Unknown setting(s): {string.Join(", ", unknown)}";
        return null;
    }

    private void ApplyConfigUpdate(JsonObject update)
    {
        var oldFolder = Config.GetString("watch_folder");
        foreach (var kv in update)
        {
            if (EditableKeys.Contains(kv.Key))
                Config[kv.Key] = kv.Value is null ? null : JsonNode.Parse(kv.Value.ToJsonString());
        }
        AppConfig.SaveConfig(Config);
        Watcher.StabilitySeconds = Config.GetDouble("stability_seconds", 3.0);
        Watcher.StabilityMaxWait = Config.GetDouble("stability_max_wait", 120.0);
        if (Config.GetString("watch_folder") != oldFolder && Watcher.IsStarted)
            Watcher.RestartObserver();
    }

    public static Dictionary<string, object?> MaskedConfig(Dictionary<string, object?> cfg)
    {
        var output = new Dictionary<string, object?>();
        foreach (var key in EditableKeys)
            if (cfg.ContainsKey(key)) output[key] = cfg[key];
        var apiKey = cfg.GetString("ionos_api_key");
        output["ionos_api_key"] = apiKey.Length > 0
            ? new string('*', Math.Max(0, apiKey.Length - 4)) + apiKey[^Math.Min(4, apiKey.Length)..]
            : "";
        return output;
    }

    private async Task PostScan(HttpListenerContext ctx)
    {
        var count = Watcher.ScanExisting();
        await SendJson(ctx, new { ok = true, queued = count });
    }
}
