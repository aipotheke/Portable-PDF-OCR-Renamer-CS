using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PdfOcrRenamer;

public sealed class FolderWatcher : IDisposable
{
    private const int MissingPollsLimit = 20;

    private readonly BlockingCollection<string?> _queue = new(new ConcurrentQueue<string?>());
    private readonly HashSet<string> _enqueued = new();
    private readonly Dictionary<string, Dictionary<string, object?>> _jobs = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _fsw;
    private Thread? _worker;
    private ILogger _log;
    private readonly Func<string, Task<string?>>? _processAsync;
    private readonly bool _requireApiKey;

    public bool Paused;
    public double StabilitySeconds;
    public double StabilityMaxWait;

    public FolderWatcher(Dictionary<string, object?> cfg, Func<string, Task<string?>>? processAsync = null,
        bool requireApiKey = true, ILogger? log = null)
    {
        Config = cfg;
        _processAsync = processAsync;
        _requireApiKey = requireApiKey;
        _log = log ?? new ConsoleLogger();
        StabilitySeconds = cfg.GetDouble("stability_seconds", 3.0);
        StabilityMaxWait = cfg.GetDouble("stability_max_wait", 120.0);
    }

    public Dictionary<string, object?> Config { get; }
    public bool IsStarted { get; private set; }

    public string Folder()
    {
        var configured = Config.GetString("watch_folder");
        return configured.Length > 0 ? configured : AppConfig.ConfigDir();
    }

    public List<Dictionary<string, object?>> JobList()
    {
        lock (_lock) return _jobs.Values.Select(j => new Dictionary<string, object?>(j)).ToList();
    }

    public bool Enqueue(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        var key = Path.GetFullPath(path);
        lock (_lock)
        {
            if (_enqueued.Contains(key)) return false;
            _enqueued.Add(key);
            _jobs[key] = new Dictionary<string, object?> { ["name"] = Path.GetFileName(path), ["stage"] = "queued" };
        }
        _queue.Add(path);
        _log.Info($"Queued: {Path.GetFileName(path)}");
        return true;
    }

    public int ScanExisting()
    {
        var count = 0;
        var folder = Folder();
        if (Directory.Exists(folder))
        {
            foreach (var p in Directory.GetFiles(folder, "*.pdf").OrderBy(p => p))
                if (Enqueue(p)) count++;
        }
        return count;
    }

    public void Start()
    {
        if (IsStarted) return;
        _fsw = new FileSystemWatcher(Folder())
        {
            Filter = "*.pdf",
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _fsw.Created += (_, e) => Enqueue(e.FullPath);
        _fsw.Changed += (_, e) => Enqueue(e.FullPath);
        _fsw.Renamed += (_, e) => { if (e.FullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) Enqueue(e.FullPath); };
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ocr-worker" };
        _worker.Start();
        IsStarted = true;
        _log.Info($"Watching folder: {Folder()}");
    }

    public void Pause() { Paused = true; _log.Info("Watcher paused"); }
    public void Resume() { Paused = false; _log.Info("Watcher resumed"); }

    public void RestartObserver()
    {
        if (_fsw is null) return;
        _fsw.Dispose();
        _fsw = null;
        _fsw = new FileSystemWatcher(Folder())
        {
            Filter = "*.pdf",
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _fsw.Created += (_, e) => Enqueue(e.FullPath);
        _fsw.Changed += (_, e) => Enqueue(e.FullPath);
        _fsw.Renamed += (_, e) => { if (e.FullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) Enqueue(e.FullPath); };
        _log.Info($"Now watching folder: {Folder()}");
    }

    public void Stop()
    {
        if (!IsStarted) return;
        _cts.Cancel();
        _fsw?.Dispose();
        _fsw = null;
        _queue.Add(null);
        _worker?.Join(TimeSpan.FromSeconds(10));
        _worker = null;
        IsStarted = false;
        _log.Info("Watcher stopped");
    }

    public void Dispose() => Stop();

    private async void WorkerLoop()
    {
        var stop = _cts.Token;
        foreach (var item in _queue.GetConsumingEnumerable(stop))
        {
            if (item is null) break;
            var path = item;
            try
            {
                while (Paused && !stop.IsCancellationRequested) Thread.Sleep(200);
                if (stop.IsCancellationRequested) break;
                Handle(path, stop);
            }
            catch (Exception exc)
            {
                _log.Error($"Unexpected worker error for {path}", exc);
                Record(path, "error", new Dictionary<string, object?> { ["error"] = "unexpected worker error" });
            }
            finally
            {
                lock (_lock) _enqueued.Remove(Path.GetFullPath(path));
            }
        }
    }

    private async Task Handle(string path, CancellationToken stop)
    {
        _log.Info($"New PDF: {Path.GetFileName(path)}");
        Record(path, "waiting_stable");
        while (_requireApiKey && AppConfig.GetApiKey(Config).Length == 0 && !stop.IsCancellationRequested)
        {
            var stage = JobStage(path);
            if (stage != "waiting_for_key")
            {
                _log.Info($"No API key set — idling: {Path.GetFileName(path)}");
                Record(path, "waiting_for_key");
            }
            Thread.Sleep(1000);
        }
        if (stop.IsCancellationRequested) return;

        if (!WaitForStability(path, StabilitySeconds, StabilityMaxWait, stop))
        {
            if (stop.IsCancellationRequested) return;
            var msg = "file never stabilized (still being written or disappeared)";
            _log.Error($"Skipping {Path.GetFileName(path)}: {msg}");
            Record(path, "error", new Dictionary<string, object?> { ["error"] = msg });
            return;
        }

        var (already, prev) = Rules.IsProcessed(path);
        if (already)
        {
            _log.Info($"Skipping already-processed file: {Path.GetFileName(path)} (→ {prev})");
            Record(path, "skipped", new Dictionary<string, object?> { ["result"] = prev });
            return;
        }

        Record(path, "processing");
        string? output = null;
        try
        {
            output = _processAsync is not null
                ? await _processAsync(path)
                : await Program.ProcessOneAsync(path, Config, _log);
        }
        catch (Exception exc)
        {
            _log.Error($"Processing failed: {Path.GetFileName(path)}", exc);
            Record(path, "error", new Dictionary<string, object?> { ["error"] = exc.Message });
            return;
        }
        Record(path, "done", new Dictionary<string, object?> { ["result"] = output });
        _log.Info($"Job done: {Path.GetFileName(path)} → {output}");
    }

    private void Record(string path, string stage, Dictionary<string, object?>? fields = null)
    {
        lock (_lock)
        {
            var key = Path.GetFullPath(path);
            if (!_jobs.TryGetValue(key, out var job))
                _jobs[key] = job = new Dictionary<string, object?> { ["name"] = Path.GetFileName(path) };
            job["stage"] = stage;
            job["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (fields is not null)
                foreach (var kv in fields) job[kv.Key] = kv.Value;
        }
    }

    private string? JobStage(string path)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(Path.GetFullPath(path), out var job) && job.TryGetValue("stage", out var s) ? s?.ToString() : null;
        }
    }

    public static (long Size, long Mtime)? FileFingerprint(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return (fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds());
        }
        catch { return null; }
    }

    public static bool WaitForStability(string path, double stabilitySeconds = 3.0, double maxWait = 120.0,
        CancellationToken stop = default, double pollInterval = 0.5)
    {
        var sw = Stopwatch.StartNew();
        var deadline = sw.ElapsedMilliseconds + maxWait * 1000;
        (long, long)? last = null;
        var stableSince = 0.0;
        var missing = 0;
        while (sw.ElapsedMilliseconds < deadline)
        {
            if (stop.IsCancellationRequested) return false;
            var fp = FileFingerprint(path);
            var elapsed = sw.Elapsed.TotalSeconds;
            if (fp is null)
            {
                missing++;
                if (missing >= MissingPollsLimit) return false;
                last = null;
            }
            else
            {
                missing = 0;
                if (fp.Equals(last) && elapsed - stableSince >= stabilitySeconds) return true;
                if (!fp.Equals(last)) { last = fp; stableSince = elapsed; }
            }
            var remaining = (deadline - sw.ElapsedMilliseconds) / 1000.0;
            if (remaining <= 0) break;
            Thread.Sleep((int)(Math.Min(pollInterval, remaining) * 1000));
        }
        return false;
    }
}
