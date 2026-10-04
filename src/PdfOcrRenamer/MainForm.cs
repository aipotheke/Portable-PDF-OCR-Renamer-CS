using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PdfOcrRenamer
{
    public sealed class MainForm : Form
    {
        private readonly FolderWatcher _watcher;
        private readonly Dictionary<string, object> _config;
        private readonly ILogger _log;
        private readonly System.Windows.Forms.Timer _poll;
        private readonly ListView _jobs;
        private readonly Label _status;
        private readonly TrayIcon _tray;
        private TextBox _apiKey;
        private TextBox _watchFolder;
        private TextBox _docTypes;
        private CheckBox _senderInName;
        private CheckBox _keepSidecar;
        private Button _pauseButton;
        private Button _saveButton;
        private Button _browseButton;
        private Button _scanButton;
        private Label _saveMsg;

        public MainForm(Dictionary<string, object> config, FolderWatcher watcher, ILogger log, TrayIcon tray)
        {
            _config = config;
            _watcher = watcher;
            _log = log;
            _tray = tray;
            if (_tray != null) _tray.ShowRequested += ShowFromTray;
            Text = "PDF OCR Renamer";
            MinimumSize = new Size(720, 560);
            StartPosition = FormStartPosition.CenterScreen;

            _status = new Label();
            _status.Dock = DockStyle.Top;
            _status.Height = 28;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(6, 6, 0, 0);
            _jobs = new ListView();
            _jobs.Dock = DockStyle.Fill;
            _jobs.View = View.Details;
            _jobs.FullRowSelect = true;
            _jobs.GridLines = true;
            _jobs.Columns.Add("File", 220);
            _jobs.Columns.Add("Stage", 110);
            _jobs.Columns.Add("Result / Error", 260);
            _jobs.Columns.Add("Updated", 140);

            var settings = BuildSettingsPanel();

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(_status, 0, 0);
            root.Controls.Add(_jobs, 0, 1);
            root.Controls.Add(settings, 0, 2);
            Controls.Add(root);

            _poll = new System.Windows.Forms.Timer();
            _poll.Interval = 2000;
            _poll.Tick += delegate(object s, EventArgs e) { RefreshJobs(); };
            _poll.Start();
            RefreshJobs();
        }

        private Panel BuildSettingsPanel()
        {
            var panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(6);
            panel.AutoSize = true;

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Top;
            grid.ColumnCount = 2;
            grid.AutoSize = true;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _apiKey = new TextBox();
            _apiKey.UseSystemPasswordChar = true;
            _apiKey.Text = _config.GetString("ionos_api_key");
            _watchFolder = new TextBox();
            _watchFolder.Text = _config.GetString("watch_folder");
            _docTypes = new TextBox();
            _docTypes.Text = string.Join(", ", _config.GetList("doc_types").ToArray());
            _senderInName = new CheckBox();
            _senderInName.Text = "Include sender company in filename";
            _senderInName.Checked = _config.GetBool("sender_in_filename", true);
            _senderInName.Dock = DockStyle.Fill;
            _keepSidecar = new CheckBox();
            _keepSidecar.Text = "Write Markdown sidecar to md/";
            _keepSidecar.Checked = _config.GetBool("keep_md_sidecar", true);
            _keepSidecar.Dock = DockStyle.Fill;

            var apiKeyLabel = new Label();
            apiKeyLabel.Text = "IONOS API key:";
            apiKeyLabel.TextAlign = ContentAlignment.MiddleRight;
            apiKeyLabel.Dock = DockStyle.Fill;
            var folderLabel = new Label();
            folderLabel.Text = "Watch folder:";
            folderLabel.TextAlign = ContentAlignment.MiddleRight;
            folderLabel.Dock = DockStyle.Fill;
            var typesLabel = new Label();
            typesLabel.Text = "Document types (comma-separated):";
            typesLabel.TextAlign = ContentAlignment.MiddleRight;
            typesLabel.Dock = DockStyle.Fill;

            AddRow(grid, apiKeyLabel, _apiKey);
            AddRow(grid, folderLabel, _watchFolder);
            AddRow(grid, typesLabel, _docTypes);
            AddRow(grid, new Label(), _senderInName);
            AddRow(grid, new Label(), _keepSidecar);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.Dock = DockStyle.Fill;
            buttons.AutoSize = true;
            buttons.Padding = new Padding(0, 8, 0, 0);
            _saveButton = new Button();
            _saveButton.Text = "Save";
            _saveButton.AutoSize = true;
            _scanButton = new Button();
            _scanButton.Text = "Process existing PDFs";
            _scanButton.AutoSize = true;
            _pauseButton = new Button();
            _pauseButton.Text = "Pause";
            _pauseButton.AutoSize = true;
            _browseButton = new Button();
            _browseButton.Text = "Browse...";
            _browseButton.AutoSize = true;
            _saveMsg = new Label();
            _saveMsg.Text = "";
            _saveMsg.AutoSize = true;
            _saveMsg.Padding = new Padding(8, 8, 0, 0);
            _saveButton.Click += delegate(object s, EventArgs e) { SaveConfig(); };
            _scanButton.Click += delegate(object s, EventArgs e)
            {
                var n = _watcher.ScanExisting();
                Status("Queued " + n + " existing PDF(s).");
            };
            _pauseButton.Click += delegate(object s, EventArgs e) { TogglePause(); };
            _browseButton.Click += delegate(object s, EventArgs e) { BrowseFolder(); };
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

        private static void AddRow(TableLayoutPanel grid, Control label, Control input)
        {
            grid.RowCount++;
            grid.Controls.Add(label, 0, grid.RowCount - 1);
            grid.Controls.Add(input, 1, grid.RowCount - 1);
            input.Dock = DockStyle.Fill;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (Directory.Exists(_watchFolder.Text)) dialog.SelectedPath = _watchFolder.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _watchFolder.Text = dialog.SelectedPath;
            }
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
                SaveFail("API key must not be empty");
                return;
            }
            var folder = _watchFolder.Text.Trim();
            if (folder.Length > 0 && !Directory.Exists(folder))
            {
                SaveFail("Watch folder does not exist: " + folder);
                return;
            }
            var parts = _docTypes.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var types = new List<object>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) types.Add(t);
            }
            if (types.Count == 0)
            {
                SaveFail("At least one document type is required");
                return;
            }

            var oldFolder = _config.GetString("watch_folder");
            _config["ionos_api_key"] = apiKey;
            _config["watch_folder"] = folder;
            _config["doc_types"] = types;
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

        private void SaveFail(string message)
        {
            _saveMsg.Text = message;
            _saveMsg.ForeColor = Color.Firebrick;
        }

        private void Status(string text) { _status.Text = text; }

        private void RefreshJobs()
        {
            var paused = _watcher.Paused;
            var hasKey = AppConfig.GetApiKey(_config).Length > 0;
            Status((paused ? "\u23f8 Paused." : "\u25b6 Watching.") + "  Folder: " + _watcher.Folder() +
                   (hasKey ? "" : "   \u2014   Setup: enter your IONOS API key below and save."));

            _pauseButton.Text = paused ? "Resume" : "Pause";

            var jobs = _watcher.JobList();
            _jobs.BeginUpdate();
            _jobs.Items.Clear();
            foreach (var j in jobs)
            {
                var item = new ListViewItem(GetStr(j, "name"));
                item.SubItems.Add(GetStr(j, "stage"));
                var detail = j.ContainsKey("error") && j["error"] != null ? j["error"].ToString()
                    : j.TryGetValue("result", out var r) && r != null ? r.ToString() : "";
                item.SubItems.Add(detail);
                item.SubItems.Add(GetStr(j, "updated"));
                var stage = GetStr(j, "stage");
                if (stage == "done") item.ForeColor = Color.SeaGreen;
                else if (stage == "error") item.ForeColor = Color.Firebrick;
                else if (stage == "skipped") item.ForeColor = Color.Gray;
                _jobs.Items.Add(item);
            }
            _jobs.EndUpdate();

            if (_tray != null)
            {
                var info = new TrayInfo();
                info.Folder = Path.GetFileName(_watcher.Folder());
                if (info.Folder.Length == 0) info.Folder = _watcher.Folder();
                info.Queued = _watcher.QueueCount();
                info.Active = _watcher.ActiveCount();
                info.Done = _watcher.DoneCount();
                info.Paused = _watcher.Paused;
                info.HasApiKey = hasKey;
                _tray.Refresh();
            }
        }

        private static string GetStr(Dictionary<string, object> dict, string key)
        {
            object v;
            return dict.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && _tray != null)
            {
                Hide();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _poll.Stop();
            base.OnFormClosed(e);
        }
    }
}
