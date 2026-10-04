using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PdfOcrRenamer
{
    public sealed class FolderWatcher : IDisposable
    {
        private const int MissingPollsLimit = 20;

        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private readonly HashSet<string> _enqueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, object>> _jobs = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private FileSystemWatcher _fsw;
        private Thread _worker;
        private ILogger _log;
        private readonly Func<string, Task<string>> _processAsync;
        private readonly bool _requireApiKey;

        public bool Paused;
        public double StabilitySeconds;
        public double StabilityMaxWait;

        public FolderWatcher(Dictionary<string, object> cfg, Func<string, Task<string>> processAsync = null,
            bool requireApiKey = true, ILogger log = null)
        {
            Config = cfg;
            _processAsync = processAsync;
            _requireApiKey = requireApiKey;
            _log = log ?? new ConsoleLogger();
            StabilitySeconds = cfg.GetDouble("stability_seconds", 3.0);
            StabilityMaxWait = cfg.GetDouble("stability_max_wait", 120.0);
        }

        public Dictionary<string, object> Config { get; }
        public bool IsStarted { get; private set; }

        public string Folder()
        {
            var configured = Config.GetString("watch_folder");
            return configured.Length > 0 ? configured : AppConfig.ConfigDir();
        }

        public List<Dictionary<string, object>> JobList()
        {
            lock (_lock)
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var j in _jobs.Values)
                {
                    var copy = new Dictionary<string, object>();
                    foreach (var kv in j) copy[kv.Key] = kv.Value;
                    list.Add(copy);
                }
                return list;
            }
        }

        public int QueueCount()
        {
            lock (_lock)
            {
                var n = 0;
                foreach (var j in _jobs.Values)
                {
                    var stage = j.TryGetValue("stage", out var s) && s != null ? s.ToString() : "";
                    if (stage == "queued") n++;
                }
                return n;
            }
        }

        public int ActiveCount()
        {
            lock (_lock)
            {
                var n = 0;
                foreach (var j in _jobs.Values)
                {
                    var stage = j.TryGetValue("stage", out var s) && s != null ? s.ToString() : "";
                    if (stage == "processing" || stage == "waiting_stable" || stage == "waiting_for_key") n++;
                }
                return n;
            }
        }

        public int DoneCount()
        {
            lock (_lock)
            {
                var n = 0;
                foreach (var j in _jobs.Values)
                {
                    var stage = j.TryGetValue("stage", out var s) && s != null ? s.ToString() : "";
                    if (stage == "done") n++;
                }
                return n;
            }
        }

        public bool Enqueue(string path)
        {
            if (path == null || !File.Exists(path) || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
            var key = Path.GetFullPath(path);
            lock (_lock)
            {
                if (_enqueued.Contains(key)) return false;
                _enqueued.Add(key);
                var job = new Dictionary<string, object>();
                job["name"] = Path.GetFileName(path);
                job["stage"] = "queued";
                _jobs[key] = job;
            }
            _queue.Add(path);
            _log.Info("Queued: " + Path.GetFileName(path));
            return true;
        }

        public int ScanExisting()
        {
            var count = 0;
            var folder = Folder();
            if (Directory.Exists(folder))
            {
                foreach (var p in Directory.GetFiles(folder, "*.pdf").OrderBy(f => f))
                    if (Enqueue(p)) count++;
            }
            return count;
        }

        private void WireEvents(FileSystemWatcher fsw)
        {
            fsw.Created += delegate(object s, FileSystemEventArgs e) { Enqueue(e.FullPath); };
            fsw.Changed += delegate(object s, FileSystemEventArgs e) { Enqueue(e.FullPath); };
            fsw.Renamed += delegate(object s, RenamedEventArgs e)
            {
                if (e.FullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) Enqueue(e.FullPath);
            };
        }

        public void Start()
        {
            if (IsStarted) return;
            _fsw = new FileSystemWatcher(Folder());
            _fsw.Filter = "*.pdf";
            _fsw.IncludeSubdirectories = false;
            _fsw.EnableRaisingEvents = true;
            WireEvents(_fsw);
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "ocr-worker";
            _worker.Start();
            IsStarted = true;
            _log.Info("Watching folder: " + Folder());
        }

        public void Pause() { Paused = true; _log.Info("Watcher paused"); }
        public void Resume() { Paused = false; _log.Info("Watcher resumed"); }

        public void RestartObserver()
        {
            if (_fsw == null) return;
            _fsw.Dispose();
            _fsw = new FileSystemWatcher(Folder());
            _fsw.Filter = "*.pdf";
            _fsw.IncludeSubdirectories = false;
            _fsw.EnableRaisingEvents = true;
            WireEvents(_fsw);
            _log.Info("Now watching folder: " + Folder());
        }

        public void Stop()
        {
            if (!IsStarted) return;
            _cts.Cancel();
            if (_fsw != null) { _fsw.Dispose(); _fsw = null; }
            _queue.Add(null);
            if (_worker != null) { _worker.Join(TimeSpan.FromSeconds(10)); _worker = null; }
            IsStarted = false;
            _log.Info("Watcher stopped");
        }

        public void Dispose() { Stop(); }

        private void WorkerLoop()
        {
            var stop = _cts.Token;
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (item == null || stop.IsCancellationRequested) break;
                var path = item;
                try
                {
                    while (Paused && !stop.IsCancellationRequested) Thread.Sleep(200);
                    if (stop.IsCancellationRequested) break;
                    Handle(path, stop).Wait();
                }
                catch (Exception exc)
                {
                    _log.Error("Unexpected worker error for " + path, exc);
                    var fields = new Dictionary<string, object>();
                    fields["error"] = "unexpected worker error";
                    Record(path, "error", fields);
                }
                finally
                {
                    lock (_lock) _enqueued.Remove(Path.GetFullPath(path));
                }
            }
        }

        private async Task Handle(string path, CancellationToken stop)
        {
            _log.Info("New PDF: " + Path.GetFileName(path));
            Record(path, "waiting_stable", null);
            while (_requireApiKey && AppConfig.GetApiKey(Config).Length == 0 && !stop.IsCancellationRequested)
            {
                var stage = JobStage(path);
                if (stage != "waiting_for_key")
                {
                    _log.Info("No API key set — idling: " + Path.GetFileName(path));
                    Record(path, "waiting_for_key", null);
                }
                Thread.Sleep(1000);
            }
            if (stop.IsCancellationRequested) return;

            if (!WaitForStability(path, StabilitySeconds, StabilityMaxWait, stop, 0.5))
            {
                if (stop.IsCancellationRequested) return;
                var msg = "file never stabilized (still being written or disappeared)";
                _log.Error("Skipping " + Path.GetFileName(path) + ": " + msg);
                var errFields = new Dictionary<string, object>();
                errFields["error"] = msg;
                Record(path, "error", errFields);
                return;
            }

            string prev;
            var already = Rules.IsProcessed(path, out prev);
            if (already)
            {
                _log.Info("Skipping already-processed file: " + Path.GetFileName(path) + " (→ " + prev + ")");
                var skipFields = new Dictionary<string, object>();
                skipFields["result"] = prev;
                Record(path, "skipped", skipFields);
                return;
            }

            Record(path, "processing", null);
            string output = null;
            try
            {
                output = _processAsync != null
                    ? await _processAsync(path)
                    : await Program.ProcessOneAsync(path, Config, _log);
            }
            catch (Exception exc)
            {
                _log.Error("Processing failed: " + Path.GetFileName(path), exc);
                var exFields = new Dictionary<string, object>();
                exFields["error"] = exc.Message;
                Record(path, "error", exFields);
                return;
            }
            var doneFields = new Dictionary<string, object>();
            doneFields["result"] = output;
            Record(path, "done", doneFields);
            _log.Info("Job done: " + Path.GetFileName(path) + " → " + output);
        }

        private void Record(string path, string stage, Dictionary<string, object> fields)
        {
            lock (_lock)
            {
                var key = Path.GetFullPath(path);
                Dictionary<string, object> job;
                if (!_jobs.TryGetValue(key, out job))
                {
                    job = new Dictionary<string, object>();
                    job["name"] = Path.GetFileName(path);
                    _jobs[key] = job;
                }
                job["stage"] = stage;
                job["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (fields != null)
                    foreach (var kv in fields) job[kv.Key] = kv.Value;
            }
        }

        private string JobStage(string path)
        {
            lock (_lock)
            {
                Dictionary<string, object> job;
                object s;
                if (_jobs.TryGetValue(Path.GetFullPath(path), out job) && job.TryGetValue("stage", out s) && s != null)
                    return s.ToString();
                return null;
            }
        }

        public static FileFingerprintResult FileFingerprint(string path)
        {
            var result = new FileFingerprintResult();
            try
            {
                var fi = new FileInfo(path);
                result.Exists = true;
                result.Size = fi.Length;
                result.Mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            }
            catch { }
            return result;
        }

        public static bool WaitForStability(string path, double stabilitySeconds = 3.0, double maxWait = 120.0,
            CancellationToken stop = default(CancellationToken), double pollInterval = 0.5)
        {
            var sw = Stopwatch.StartNew();
            var deadline = sw.ElapsedMilliseconds + (long)(maxWait * 1000);
            var haveLast = false;
            var lastValue = new FileFingerprintResult();
            var stableSince = 0.0;
            var missing = 0;
            while (sw.ElapsedMilliseconds < deadline)
            {
                if (stop.IsCancellationRequested) return false;
                var fp = FileFingerprint(path);
                var elapsed = sw.Elapsed.TotalSeconds;
                if (!fp.Exists)
                {
                    missing++;
                    if (missing >= MissingPollsLimit) return false;
                    haveLast = false;
                }
                else
                {
                    missing = 0;
                    var same = haveLast && fp.Size == lastValue.Size && fp.Mtime == lastValue.Mtime;
                    if (same && elapsed - stableSince >= stabilitySeconds) return true;
                    if (!same) { lastValue = fp; haveLast = true; stableSince = elapsed; }
                }
                var remaining = (deadline - sw.ElapsedMilliseconds) / 1000.0;
                if (remaining <= 0) break;
                Thread.Sleep((int)(Math.Min(pollInterval, remaining) * 1000));
            }
            return false;
        }
    }

    public struct FileFingerprintResult
    {
        public bool Exists;
        public long Size;
        public long Mtime;
    }
}
