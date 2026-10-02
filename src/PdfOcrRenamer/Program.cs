namespace PdfOcrRenamer;

public static class Program
{
    public const string LockFileName = "app.lock";
    public const string UiUrl = "http://127.0.0.1:8765";

    private static ILogger _log = new ConsoleLogger();

    private static string LockPath() => Path.Combine(AppConfig.ConfigDir(), LockFileName);

    public static bool AcquireLock()
    {
        var path = LockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (File.Open(path, FileMode.CreateNew, FileAccess.Write))
            {
                File.WriteAllText(path, Environment.ProcessId.ToString());
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static void ReleaseLock()
    {
        try { File.Delete(LockPath()); }
        catch { }
    }

    private static void SetupLogging(bool verbose)
    {
        var logPath = Path.Combine(AppConfig.ConfigDir(), "app.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _log = new FileLogger(logPath, verbose);
    }

    public static async Task<string?> ProcessOneAsync(string pdfPath, Dictionary<string, object?> cfg, ILogger log)
    {
        var fullPath = Path.GetFullPath(pdfPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(fullPath);

        var (already, prevName) = Rules.IsProcessed(fullPath);
        if (already)
        {
            log.Info($"Skipping already-processed file: {Path.GetFileName(fullPath)} (→ {prevName})");
            return null;
        }

        var configuredFolder = cfg.GetString("watch_folder");
        var watchFolder = Path.GetFullPath(configuredFolder.Length > 0 ? configuredFolder : Path.GetDirectoryName(fullPath)!);
        var keepSidecar = cfg.GetBool("keep_md_sidecar", true);

        log.Info($"OCR: {Path.GetFileName(fullPath)}");
        var markdown = await Ocr.OcrPdfAsync(fullPath, cfg, log);
        var (filetype, sender) = await Ocr.ClassifyAsync(markdown, cfg.GetList("doc_types"), cfg, log);
        var senderText = sender.Length > 0 ? sender : "unknown";
        log.Info($"Classified as: {filetype} (sender: {senderText})");

        var senderInName = cfg.GetBool("sender_in_filename", true);
        var output = PdfOps.EmbedAndWrite(fullPath, markdown, filetype, watchFolder, keepSidecar, senderInName ? sender : "");
        Rules.MarkProcessed(fullPath, Path.GetFileName(output));
        log.Info($"Done: {Path.GetFileName(fullPath)} → {output}");
        return output;
    }

    public static async Task<int> Main(string[] args)
    {
        string? once = null;
        var watch = false;
        var serve = false;
        var verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--once" when i + 1 < args.Length: once = args[++i]; break;
                case "--watch": watch = true; break;
                case "--serve": serve = true; break;
                case "-v" or "--verbose": verbose = true; break;
            }
        }

        if (once is not null)
        {
            var cfg = AppConfig.LoadConfig();
            try { await ProcessOneAsync(once, cfg, _log); return 0; }
            catch (FileNotFoundException) { Console.Error.WriteLine($"File not found: {once}"); return 2; }
            catch (Exception exc) { Console.Error.WriteLine($"Error: {exc.Message}"); return 1; }
        }

        if (watch)
        {
            var cfg = AppConfig.LoadConfig();
            using var cts = new CancellationTokenSource();
            using var watcher = new FolderWatcher(cfg, log: _log);
            watcher.Start();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }
            watcher.Stop();
            return 0;
        }

        if (serve)
        {
            var cfg = AppConfig.LoadConfig();
            using var cts = new CancellationTokenSource();
            using var ui = new WebUI(cfg, log: _log);
            ui.Start();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }
            ui.Stop();
            return 0;
        }

        if (!AcquireLock())
        {
            Console.WriteLine("Another instance is already running (app.lock exists) — opening its UI.");
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = UiUrl,
                    UseShellExecute = true,
                });
            }
            catch { }
            return 0;
        }

        SetupLogging(verbose);
        var quitEvent = new ManualResetEvent(false);
        var config = AppConfig.LoadConfig();
        using var ui2 = new WebUI(config, log: _log);
        ui2.Start();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quitEvent.Set(); };
        Console.WriteLine($"Running — UI at {UiUrl} (Ctrl+C to quit).");
        quitEvent.WaitOne();
        ui2.Stop();
        ReleaseLock();
        _log.Info("Bye");
        return 0;
    }
}

public sealed class FileLogger : ILogger
{
    private readonly object _lock = new();
    private readonly string _path;

    public FileLogger(string path, bool verbose)
    {
        _path = path;
    }

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            using var writer = new StreamWriter(_path, append: true);
            writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} main: {message}");
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARNING", message);
    public void Error(string message) => Write("ERROR", message);
    public void Error(string message, Exception exc) => Write("ERROR", $"{message}: {exc}");
}
