using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PdfOcrRenamer
{
    public static class Program
    {
        public const string LockFileName = "app.lock";

        private static ILogger _log = new ConsoleLogger();

        private static string LockPath() { return Path.Combine(AppConfig.ConfigDir(), LockFileName); }

        public static bool AcquireLock()
        {
            var path = LockPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs))
                    w.Write(System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
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
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            _log = new FileLogger(logPath);
        }

        public static async Task<string> ProcessOneAsync(string pdfPath, Dictionary<string, object> cfg, ILogger log)
        {
            var fullPath = Path.GetFullPath(pdfPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException(fullPath);

            string prevName;
            var already = Rules.IsProcessed(fullPath, out prevName);
            if (already)
            {
                log.Info("Skipping already-processed file: " + Path.GetFileName(fullPath) + " (→ " + prevName + ")");
                return null;
            }

            var configuredFolder = cfg.GetString("watch_folder");
            var watchFolder = Path.GetFullPath(configuredFolder.Length > 0 ? configuredFolder : Path.GetDirectoryName(fullPath));
            var keepSidecar = cfg.GetBool("keep_md_sidecar", true);

            log.Info("OCR: " + Path.GetFileName(fullPath));
            var markdown = await Ocr.OcrPdfAsync(fullPath, cfg, log);
            var classified = await Ocr.ClassifyAsync(markdown, cfg.GetList("doc_types"), cfg, log);
            var filetype = classified.DocType;
            var sender = classified.Sender;
            var senderText = sender.Length > 0 ? sender : "unknown";
            log.Info("Classified as: " + filetype + " (sender: " + senderText + ")");

            var senderInName = cfg.GetBool("sender_in_filename", true);
            var output = PdfOps.EmbedAndWrite(fullPath, markdown, filetype, watchFolder, keepSidecar, senderInName ? sender : "");
            Rules.MarkProcessed(fullPath, Path.GetFileName(output));
            log.Info("Done: " + Path.GetFileName(fullPath) + " → " + output);
            return output;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            string once = null;
            var cli = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--once" && i + 1 < args.Length) { once = args[++i]; cli = true; }
                else if (args[i] == "--cli") cli = true;
            }

            if (once != null)
            {
                var cfg = AppConfig.LoadConfig();
                try { ProcessOneAsync(once, cfg, _log).Wait(); return 0; }
                catch (AggregateException ax)
                {
                    if (ax.InnerExceptions.Count == 1 && ax.InnerExceptions[0] is FileNotFoundException)
                    { Console.Error.WriteLine("File not found: " + once); return 2; }
                    Console.Error.WriteLine("Error: " + ax.InnerException.Message);
                    return 1;
                }
                catch (Exception exc) { Console.Error.WriteLine("Error: " + exc.Message); return 1; }
            }

            if (!AcquireLock())
            {
                MessageBox.Show("Another instance is already running.", "PDF OCR Renamer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            SetupLogging();
            var config = AppConfig.LoadConfig();
            var watcher = new FolderWatcher(config, null, true, _log);
            watcher.Start();
            _log.Info("Watching folder: " + watcher.Folder());

            var exitCode = 0;
            if (cli)
            {
                var quit = new ManualResetEvent(false);
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; quit.Set(); };
                Console.WriteLine("Running (Ctrl+C to quit).");
                quit.WaitOne();
            }
            else
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var tray = new TrayIcon(delegate { return BuildTrayInfo(watcher, config); },
                    delegate { ToggleWatcher(watcher); },
                    delegate { Application.Exit(); }))
                {
                    Application.Run(new MainForm(config, watcher, _log, tray));
                }
            }

            watcher.Stop();
            watcher.Dispose();
            ReleaseLock();
            _log.Info("Bye");
            return exitCode;
        }

        private static TrayInfo BuildTrayInfo(FolderWatcher watcher, Dictionary<string, object> config)
        {
            var info = new TrayInfo();
            var folder = watcher.Folder();
            var name = Path.GetFileName(folder);
            info.Folder = name.Length > 0 ? name : folder;
            info.Queued = watcher.QueueCount();
            info.Active = watcher.ActiveCount();
            info.Done = watcher.DoneCount();
            info.Paused = watcher.Paused;
            info.HasApiKey = AppConfig.GetApiKey(config).Length > 0;
            return info;
        }

        private static void ToggleWatcher(FolderWatcher watcher)
        {
            if (watcher.Paused) watcher.Resume();
            else watcher.Pause();
        }
    }
}
