using System.Drawing;

namespace PdfOcrRenamer;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Func<bool> _isPaused;
    private readonly Action _togglePause;
    private readonly Action _quit;
    private bool _disposed;

    public TrayIcon(Func<bool> isPaused, Action togglePause, Action quit)
    {
        _isPaused = isPaused;
        _togglePause = togglePause;
        _quit = quit;
        _icon = new NotifyIcon
        {
            Text = "PDF OCR Renamer",
            Icon = MakeIcon(paused: false),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
    }

    public void Open() { }
    public void Close() => _icon.Visible = false;

    public void Refresh()
    {
        if (_disposed) return;
        var paused = _isPaused();
        _icon.Icon = MakeIcon(paused);
        _icon.Text = paused ? "PDF OCR Renamer (paused)" : "PDF OCR Renamer";
        if (_icon.ContextMenuStrip is { } menu)
            ((ToolStripMenuItem)menu.Items["pause"]!).Text = paused ? "Resume" : "Pause";
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open") { Name = "open" };
        var pause = new ToolStripMenuItem("Pause") { Name = "pause" };
        var quit = new ToolStripMenuItem("Quit") { Name = "quit" };
        open.Click += (_, _) => Open();
        pause.Click += (_, _) => { _togglePause(); Refresh(); };
        quit.Click += (_, _) => _quit();
        menu.Items.Add(open);
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);
        return menu;
    }

    private static Icon MakeIcon(bool paused)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(paused ? Color.FromArgb(200, 130, 30) : Color.FromArgb(40, 120, 90));
        g.FillRectangle(Brushes.White, 4, 3, 8, 10);
        g.FillRectangle(Brushes.Black, 5, 5, 6, 1);
        g.FillRectangle(Brushes.Black, 5, 7, 6, 1);
        g.FillRectangle(Brushes.Black, 5, 9, 4, 1);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
