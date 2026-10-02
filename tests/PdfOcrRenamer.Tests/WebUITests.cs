using System.Net;
using System.Text;
using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests;

public class WebUITests : IDisposable
{
    private readonly List<int> _ports = new();

    private (WebUI ui, int port) MakeUi(Dictionary<string, object?>? cfg = null, FolderWatcher? watcher = null)
    {
        var port = 8900 + _ports.Count;
        _ports.Add(port);
        var ui = new WebUI(cfg, watcher, port);
        ui.Start();
        return (ui, port);
    }

    public void Dispose()
    {
    }

    private static async Task<string> GetJson(HttpListener listener, int port, string path)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync($"http://127.0.0.1:{port}{path}");
    }

    [Fact]
    public async Task Status_ReturnsWatchFolderAndDocTypes()
    {
        var dir = Path.GetTempPath();
        var cfg = new Dictionary<string, object?>
        {
            ["watch_folder"] = dir,
            ["doc_types"] = new List<object?> { "invoice" },
        };
        var (ui, port) = MakeUi(cfg);
        try
        {
            var json = await GetJson(null!, port, "/api/status");
            Assert.Contains("watch_folder", json);
            Assert.Contains("invoice", json);
            Assert.Contains("paused", json);
        }
        finally { ui.Stop(); }
    }

    [Fact]
    public async Task GetConfig_MasksApiKey()
    {
        var cfg = new Dictionary<string, object?>
        {
            ["watch_folder"] = Path.GetTempPath(),
            ["ionos_api_key"] = "super-secret-key",
        };
        var (ui, port) = MakeUi(cfg);
        try
        {
            var json = await GetJson(null!, port, "/api/config");
            Assert.DoesNotContain("super-secret-key", json);
            Assert.Contains("-key", json);
        }
        finally { ui.Stop(); }
    }

    [Fact]
    public async Task PostConfig_RejectsMissingFolder()
    {
        var cfg = new Dictionary<string, object?> { ["watch_folder"] = Path.GetTempPath() };
        var (ui, port) = MakeUi(cfg);
        try
        {
            using var client = new HttpClient();
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/api/config",
                new StringContent("{\"watch_folder\":\"/definitely/not/existing\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("does not exist", body);
        }
        finally { ui.Stop(); }
    }

    [Fact]
    public async Task PostConfig_RejectsEmptyApiKey()
    {
        var cfg = new Dictionary<string, object?> { ["watch_folder"] = Path.GetTempPath() };
        var (ui, port) = MakeUi(cfg);
        try
        {
            using var client = new HttpClient();
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/api/config",
                new StringContent("{\"ionos_api_key\":\"\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { ui.Stop(); }
    }

    [Fact]
    public async Task PostConfig_RejectsUnknownKeys()
    {
        var cfg = new Dictionary<string, object?> { ["watch_folder"] = Path.GetTempPath() };
        var (ui, port) = MakeUi(cfg);
        try
        {
            using var client = new HttpClient();
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/api/config",
                new StringContent("{\"hacker_key\":\"x\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { ui.Stop(); }
    }

    [Fact]
    public async Task PostConfig_AcceptsValidUpdate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = new Dictionary<string, object?> { ["watch_folder"] = Path.GetTempPath() };
        var (ui, port) = MakeUi(cfg);
        try
        {
            using var client = new HttpClient();
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/api/config",
                new StringContent($"{{\"watch_folder\":\"{dir.Replace("\\", "\\\\")}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            ui.Stop();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task PostScan_EnqueuesExistingPdfs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.pdf"), "pdf");
        var cfg = new Dictionary<string, object?>
        {
            ["watch_folder"] = dir,
            ["stability_seconds"] = 0.1,
            ["stability_max_wait"] = 3.0,
        };
        var watcher = new FolderWatcher(cfg, p => Task.FromResult<string?>("out"), requireApiKey: false);
        var (ui, port) = MakeUi(cfg, watcher);
        try
        {
            using var client = new HttpClient();
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/api/scan", null!);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal(1, doc.RootElement.GetProperty("queued").GetInt32());
        }
        finally
        {
            ui.Stop();
            watcher.Dispose();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetIndex_ServesHtml()
    {
        var cfg = new Dictionary<string, object?> { ["watch_folder"] = Path.GetTempPath() };
        var (ui, port) = MakeUi(cfg);
        try
        {
            using var client = new HttpClient();
            var html = await client.GetStringAsync($"http://127.0.0.1:{port}/");
            Assert.Contains("<html", html);
        }
        finally { ui.Stop(); }
    }
}
