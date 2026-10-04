namespace PdfOcrRenamer;

public static class Program
{
    public const string LockFileName = "app.lock";

    private static ILogger _log = new ConsoleLogger();

    private static string LockPath() => Path.Combine(AppConfig.ConfigDir(), LockFileName);

    public static bool AcquireLock()
    {
        var path = LockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs))
                w.Write(Environment.ProcessId.ToString());
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

    private static void SetupLogging()
    {
        var logPath = Path.Combine(AppConfig.ConfigDir(), "app.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _log = new FileLogger(logPath);
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

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        string? once = null;
        var cli = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--once" when i + 1 < args.Length: once = args[++i]; cli = true; break;
                case "--cli": cli = true; break;
            }
        }

        if (once is not null)
        {
            var cfg = AppConfig.LoadConfig();
            try { await ProcessOneAsync(once, cfg, _log); return 0; }
            catch (FileNotFoundException) { Console.Error.WriteLine($"File not found: {once}"); return 2; }
            catch (Exception exc) { Console.Error.WriteLine($"Error: {exc.Message}"); return 1; }
        }

        if (!AcquireLock())
        {
            MessageBox.Show("Another instance is already running.", "PDF OCR Renamer",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        SetupLogging();
        var config = AppConfig.LoadConfig();
        using var watcher = new FolderWatcher(config, log: _log);
        watcher.Start();
        _log.Info($"Watching folder: {watcher.Folder()}");

        if (cli)
        {
            var quit = new ManualResetEvent(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
            Console.WriteLine("Running (Ctrl+C to quit).");
            quit.WaitOne();
        }
        else
        {
            ApplicationConfiguration.Initialize();
            using var tray = new TrayIcon(() => watcher.Paused, () => { }, () => Application.Exit());
            Application.Run(new MainForm(config, watcher, _log));
        }

        watcher.Stop();
        ReleaseLock();
        _log.Info("Bye");
        return 0;
    }
}
