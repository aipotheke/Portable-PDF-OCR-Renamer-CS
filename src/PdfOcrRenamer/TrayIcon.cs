using System;
using System.Drawing;
using System.Windows.Forms;

namespace PdfOcrRenamer
{
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Func<TrayInfo> _getInfo;
        private readonly Action _togglePause;
        private readonly Action _quit;
        private bool _disposed;

        public TrayIcon(Func<TrayInfo> getInfo, Action togglePause, Action quit)
        {
            _getInfo = getInfo;
            _togglePause = togglePause;
            _quit = quit;
            _icon = new NotifyIcon();
            _icon.Text = "PDF OCR Renamer";
            _icon.Icon = MakeIcon(false, false);
            _icon.Visible = true;
            _icon.ContextMenuStrip = BuildMenu();
            _icon.DoubleClick += delegate(object s, EventArgs e) { ShowApp(); };
        }

        public event Action ShowRequested;

        public void ShowApp()
        {
            var handler = ShowRequested;
            if (handler != null) handler();
        }

        public void Close() { _icon.Visible = false; }

        public void Refresh()
        {
            if (_disposed) return;
            var info = _getInfo();
            _icon.Icon = MakeIcon(info.Paused, info.Active > 0);
            var text = (info.Paused ? "\u23f8 paused" : "\u25b6 watching") + " — " + info.Folder;
            var counts = "queue: " + info.Queued + "  active: " + info.Active + "  done: " + info.Done;
            if (!info.HasApiKey) counts += "  —  no API key!";
            _icon.Text = (text + " — " + counts).ClampTo(63);
            var menu = _icon.ContextMenuStrip;
            if (menu != null)
            {
                var pauseItem = menu.Items.Find("pause", false);
                if (pauseItem.Length > 0)
                    ((ToolStripMenuItem)pauseItem[0]).Text = info.Paused ? "Resume" : "Pause";
                var statusItem = menu.Items.Find("status", false);
                if (statusItem.Length > 0)
                    ((ToolStripMenuItem)statusItem[0]).Text = counts;
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open");
            open.Name = "open";
            var pause = new ToolStripMenuItem("Pause");
            pause.Name = "pause";
            var status = new ToolStripMenuItem("queue: 0  active: 0  done: 0");
            status.Name = "status";
            status.Enabled = false;
            var quit = new ToolStripMenuItem("Quit");
            quit.Name = "quit";
            open.Click += delegate(object s, EventArgs e) { ShowApp(); };
            pause.Click += delegate(object s, EventArgs e) { _togglePause(); Refresh(); };
            quit.Click += delegate(object s, EventArgs e) { _quit(); };
            menu.Items.Add(open);
            menu.Items.Add(pause);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);
            return menu;
        }

        private static Icon MakeIcon(bool paused, bool busy)
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(paused ? Color.FromArgb(200, 130, 30) : busy ? Color.FromArgb(30, 80, 180) : Color.FromArgb(40, 120, 90));
                g.FillRectangle(Brushes.White, 4, 3, 8, 10);
                g.FillRectangle(Brushes.Black, 5, 5, 6, 1);
                g.FillRectangle(Brushes.Black, 5, 7, 6, 1);
                g.FillRectangle(Brushes.Black, 5, 9, 4, 1);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            _icon.Dispose();
        }
    }

    public struct TrayInfo
    {
        public string Folder;
        public int Queued;
        public int Active;
        public int Done;
        public bool Paused;
        public bool HasApiKey;
    }

    public static class TrayTextExtensions
    {
        public static string ClampTo(this string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
