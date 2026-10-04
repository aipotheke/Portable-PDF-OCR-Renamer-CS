using System.Drawing;

namespace PdfOcrRenamer;

public sealed class MainForm : Form
{
    private readonly FolderWatcher _watcher;
    private readonly Dictionary<string, object?> _config;
    private readonly ILogger _log;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly ListView _jobs;
    private readonly Label _status;
    private TextBox _apiKey = null!;
    private TextBox _watchFolder = null!;
    private TextBox _docTypes = null!;
    private CheckBox _senderInName = null!;
    private CheckBox _keepSidecar = null!;
    private Button _pauseButton = null!;
    private Button _saveButton = null!;
    private Button _browseButton = null!;
    private Button _scanButton = null!;
    private Label _saveMsg = null!;

    public MainForm(Dictionary<string, object?> config, FolderWatcher watcher, ILogger log)
    {
        _config = config;
        _watcher = watcher;
        _log = log;
        Text = "PDF OCR Renamer";
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;

        _status = new Label { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 6, 0, 0) };
        _jobs = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _jobs.Columns.Add("File", 220);
        _jobs.Columns.Add("Stage", 110);
        _jobs.Columns.Add("Result / Error", 260);
        _jobs.Columns.Add("Updated", 140);

        var settings = BuildSettingsPanel();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(_jobs, 0, 1);
        root.Controls.Add(settings, 0, 2);
        Controls.Add(root);

        _poll = new System.Windows.Forms.Timer { Interval = 2000 };
        _poll.Tick += (_, _) => RefreshJobs();
        _poll.Start();
        RefreshJobs();
    }

    private Panel BuildSettingsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6), AutoSize = true };

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(Control label, Control input)
        {
            grid.RowCount++;
            grid.Controls.Add(label, 0, grid.RowCount - 1);
            grid.Controls.Add(input, 1, grid.RowCount - 1);
            input.Dock = DockStyle.Fill;
        }

        _apiKey = new TextBox { UseSystemPasswordChar = true, Text = _config.GetString("ionos_api_key") };
        _watchFolder = new TextBox { Text = _config.GetString("watch_folder") };
        _docTypes = new TextBox { Text = string.Join(", ", _config.GetList("doc_types")) };
        _senderInName = new CheckBox { Text = "Include sender company in filename", Checked = _config.GetBool("sender_in_filename", true), Dock = DockStyle.Fill };
        _keepSidecar = new CheckBox { Text = "Write Markdown sidecar to md/", Checked = _config.GetBool("keep_md_sidecar", true), Dock = DockStyle.Fill };

        AddRow(new Label { Text = "IONOS API key:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, _apiKey);
        AddRow(new Label { Text = "Watch folder:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, _watchFolder);
        AddRow(new Label { Text = "Document types (comma-separated):", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, _docTypes);
        AddRow(new Label(), _senderInName);
        AddRow(new Label(), _keepSidecar);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        _saveButton = new Button { Text = "Save", AutoSize = true };
        _scanButton = new Button { Text = "Process existing PDFs", AutoSize = true };
        _pauseButton = new Button { Text = "Pause", AutoSize = true };
        _browseButton = new Button { Text = "Browse...", AutoSize = true };
        _saveMsg = new Label { Text = "", AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
        _saveButton.Click += (_, _) => SaveConfig();
        _scanButton.Click += (_, _) => { var n = _watcher.ScanExisting(); Status($"Queued {n} existing PDF(s)."); };
        _pauseButton.Click += (_, _) => TogglePause();
        _browseButton.Click += (_, _) => BrowseFolder();
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_scanButton);
        buttons.Controls.Add(_pauseButton);
        buttons.Controls.Add(_browseButton);
        buttons.Controls.Add(_saveMsg);

        grid.RowCount++;
        grid.Controls.Add(buttons, 0, grid.RowCount - 1);
        grid.SetColumnSpan(buttons, 2);

        panel.Controls.Add(grid);
        return panel;
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog();
        if (Directory.Exists(_watchFolder.Text)) dialog.SelectedPath = _watchFolder.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _watchFolder.Text = dialog.SelectedPath;
    }

    private void TogglePause()
    {
        if (_watcher.Paused) { _watcher.Resume(); _pauseButton.Text = "Pause"; }
        else { _watcher.Pause(); _pauseButton.Text = "Resume"; }
        RefreshJobs();
    }

    private void SaveConfig()
    {
        var apiKey = _apiKey.Text.Trim();
        if (apiKey.Length == 0)
        {
            _saveMsg.Text = "API key must not be empty";
            _saveMsg.ForeColor = Color.Firebrick;
            return;
        }
        var folder = _watchFolder.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(folder))
        {
            _saveMsg.Text = $"Watch folder does not exist: {folder}";
            _saveMsg.ForeColor = Color.Firebrick;
            return;
        }
        var types = _docTypes.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (types.Count == 0)
        {
            _saveMsg.Text = "At least one document type is required";
            _saveMsg.ForeColor = Color.Firebrick;
            return;
        }

        var oldFolder = _config.GetString("watch_folder");
        _config["ionos_api_key"] = apiKey;
        _config["watch_folder"] = folder;
        _config["doc_types"] = types.Cast<object?>().ToList();
        _config["sender_in_filename"] = _senderInName.Checked;
        _config["keep_md_sidecar"] = _keepSidecar.Checked;
        AppConfig.SaveConfig(_config);

        _watcher.StabilitySeconds = _config.GetDouble("stability_seconds", 3.0);
        _watcher.StabilityMaxWait = _config.GetDouble("stability_max_wait", 120.0);
        if (folder != oldFolder && _watcher.IsStarted) _watcher.RestartObserver();

        _saveMsg.Text = "Saved";
        _saveMsg.ForeColor = Color.SeaGreen;
        _log.Info("Config updated via WinForms UI");
        RefreshJobs();
    }

    private void Status(string text) => _status.Text = text;

    private void RefreshJobs()
    {
        var paused = _watcher.Paused;
        var hasKey = AppConfig.GetApiKey(_config).Length > 0;
        Status($"{(paused ? "⏸ Paused." : "▶ Watching.")}  Folder: {_watcher.Folder()}" +
               (hasKey ? "" : "   —   Setup: enter your IONOS API key below and save."));

        _pauseButton.Text = paused ? "Resume" : "Pause";

        var jobs = _watcher.JobList();
        _jobs.BeginUpdate();
        _jobs.Items.Clear();
        foreach (var j in jobs)
        {
            var item = new ListViewItem(j.TryGetValue("name", out var n) ? n?.ToString() : "");
            item.SubItems.Add(j.TryGetValue("stage", out var s) ? s?.ToString() : "");
            var detail = j.ContainsKey("error") ? j["error"]?.ToString() : j.TryGetValue("result", out var r) ? r?.ToString() : "";
            item.SubItems.Add(detail ?? "");
            item.SubItems.Add(j.TryGetValue("updated", out var u) ? u?.ToString() : "");
            if (j.TryGetValue("stage", out var st))
            {
                item.ForeColor = st?.ToString() switch
                {
                    "done" => Color.SeaGreen,
                    "error" => Color.Firebrick,
                    "skipped" => Color.Gray,
                    _ => Color.Black,
                };
            }
            _jobs.Items.Add(item);
        }
        _jobs.EndUpdate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        base.OnFormClosed(e);
    }
}
