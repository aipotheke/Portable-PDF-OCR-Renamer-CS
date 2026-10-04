using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests
{
    public class WatcherTests
    {
        private static Dictionary<string, object> Cfg(string folder, double stabilitySeconds)
        {
            var cfg = new Dictionary<string, object>();
            cfg["watch_folder"] = folder;
            cfg["stability_seconds"] = stabilitySeconds;
            cfg["stability_max_wait"] = 5.0;
            return cfg;
        }

        [Fact]
        public void Enqueue_RejectsNonPdfAndMissing()
        {
            using (var watcher = new FolderWatcher(Cfg(Path.GetTempPath(), 0.1), null, false, null))
            {
                Assert.False(watcher.Enqueue(Path.Combine(Path.GetTempPath(), "nope.pdf")));
                Assert.False(watcher.Enqueue(Path.GetTempPath()));
            }
        }

        [Fact]
        public void Enqueue_Deduplicates()
        {
            var dir = Path.Combine(Path.GetTempPath(), "w_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var pdf = Path.Combine(dir, "a.pdf");
            File.WriteAllText(pdf, "pdf");
            try
            {
                using (var watcher = new FolderWatcher(Cfg(dir, 0.1), null, false, null))
                {
                    Assert.True(watcher.Enqueue(pdf));
                    Assert.False(watcher.Enqueue(pdf));
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Worker_ProcessesFileEndToEnd()
        {
            var dir = Path.Combine(Path.GetTempPath(), "w_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var pdf = Path.Combine(dir, "doc.pdf");
            File.WriteAllText(pdf, "pdf");
            try
            {
                using (var watcher = new FolderWatcher(Cfg(dir, 0.1),
                    delegate { return Task.FromResult("processed/out.pdf"); }, false, null))
                {
                    watcher.Start();
                    Assert.True(watcher.Enqueue(pdf));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline)
                    {
                        var jobs = watcher.JobList();
                        if (jobs.Count > 0 && (string)jobs[0]["stage"] == "done") break;
                        Thread.Sleep(100);
                    }
                    var final = watcher.JobList();
                    Assert.Single(final);
                    Assert.Equal("done", (string)final[0]["stage"]);
                    Assert.Equal("processed/out.pdf", (string)final[0]["result"]);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Worker_RecordsErrorOnFailure()
        {
            var dir = Path.Combine(Path.GetTempPath(), "w_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var pdf = Path.Combine(dir, "bad.pdf");
            File.WriteAllText(pdf, "pdf");
            try
            {
                using (var watcher = new FolderWatcher(Cfg(dir, 0.1),
                    delegate { throw new InvalidOperationException("boom"); }, false, null))
                {
                    watcher.Start();
                    Assert.True(watcher.Enqueue(pdf));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline)
                    {
                        var jobs = watcher.JobList();
                        if (jobs.Count > 0 && (string)jobs[0]["stage"] == "error") break;
                        Thread.Sleep(100);
                    }
                    var final = watcher.JobList();
                    Assert.Single(final);
                    Assert.Equal("error", (string)final[0]["stage"]);
                    Assert.Equal("boom", (string)final[0]["error"]);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void WaitForStability_TrueWhenFileUnchanged()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "stable");
            try { Assert.True(FolderWatcher.WaitForStability(path, 0.05, 3, CancellationToken.None, 0.5)); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void WaitForStability_FalseWhenFileMissing()
        {
            var path = Path.Combine(Path.GetTempPath(), "gone.pdf");
            Assert.False(FolderWatcher.WaitForStability(path, 0.05, 1, CancellationToken.None, 0.5));
        }

        [Fact]
        public void ScanExisting_EnqueuesAllPdfs()
        {
            var dir = Path.Combine(Path.GetTempPath(), "w_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.pdf"), "pdf");
            File.WriteAllText(Path.Combine(dir, "b.pdf"), "pdf");
            File.WriteAllText(Path.Combine(dir, "c.txt"), "text");
            try
            {
                using (var watcher = new FolderWatcher(Cfg(dir, 0.1), null, false, null))
                {
                    var count = watcher.ScanExisting();
                    Assert.Equal(2, count);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Pause_BlocksProcessingUntilResume()
        {
            var dir = Path.Combine(Path.GetTempPath(), "w_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var pdf = Path.Combine(dir, "p.pdf");
            File.WriteAllText(pdf, "pdf");
            try
            {
                using (var watcher = new FolderWatcher(Cfg(dir, 0.1),
                    delegate { return Task.FromResult("x.pdf"); }, false, null))
                {
                    watcher.Start();
                    watcher.Pause();
                    Assert.True(watcher.Enqueue(pdf));
                    Thread.Sleep(400);
                    Assert.Equal("queued", (string)watcher.JobList().Single()["stage"]);
                    watcher.Resume();
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline && (string)watcher.JobList().Single()["stage"] != "done")
                        Thread.Sleep(100);
                    Assert.Equal("done", (string)watcher.JobList().Single()["stage"]);
                }
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
