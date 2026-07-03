using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>
    /// Themed drag-and-drop send dialog. For each image/video the user chooses
    /// "Compress" or "Send as file"; other files are always sent as a file. On Send,
    /// uploads each file via <see cref="MediaSender"/> with per-file progress + cancel.
    ///
    /// The dialog owns the upload/send; it raises FileStarting/FileSucceeded/FileFailed
    /// (on the UI thread) so MainForm can show optimistic bubbles and swap them on the
    /// server-confirmed id (mirroring the text-send dedup via _shownMessageIds).
    /// </summary>
    public class SendMediaDialog : MaterialForm
    {
        private sealed class Row
        {
            public string Path;
            public SendMode Mode;
            public bool Toggleable;       // image/video can switch Compress↔File
            public SendMode CompressMode; // Photo or Video (the non-file option)
            public Panel Panel;
            public MaterialButton ModeButton;
            public ProgressBar Bar;
            public MaterialLabel Info;   // shows size, then upload status
        }

        private readonly MaterialSkinManager _skin;
        private readonly WTelegram.Client _client;
        private readonly TL.InputPeer _peer;
        private readonly List<Row> _rows = new List<Row>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _sending;

        private FlowLayoutPanel _list;
        private MaterialTextBox2 _caption;
        private MaterialButton _sendBtn, _cancelBtn;

        /// <summary>Raised (UI thread) just before a file's upload begins. (token, path, mode, caption)</summary>
        public event Action<int, string, SendMode, string> FileStarting;
        /// <summary>Raised (UI thread) when a file is confirmed sent. (token, message)</summary>
        public event Action<int, TL.Message> FileSucceeded;
        /// <summary>Raised (UI thread) when a file failed/cancelled. (token, error)</summary>
        public event Action<int, string> FileFailed;

        /// <summary>Raised (UI thread) before an album (≥2 grouped items) uploads. (token0, paths, caption)</summary>
        public event Action<int, IList<string>, string> AlbumSending;
        /// <summary>Raised (UI thread) when an album is confirmed sent. (token0, messages)</summary>
        public event Action<int, TL.Message[]> AlbumSent;
        /// <summary>Raised (UI thread) when an album failed/cancelled. (token0, error)</summary>
        public event Action<int, string> AlbumFailed;

        public SendMediaDialog(WTelegram.Client client, TL.InputPeer peer, IList<string> paths)
        {
            _client = client;
            _peer = peer;

            _skin = MaterialSkinManager.Instance;
            _skin.AddFormToManage(this);
            ApplyTheme();

            BuildUi(paths);
            ThemeHelper.ThemeChanged += OnThemeChanged;
            FormClosed += (s, e) => ThemeHelper.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { ApplyTheme(); RecolorRows(); })); } catch { }
        }

        private void ApplyTheme()
        {
            _skin.Theme = ThemeHelper.IsDark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
            var win = ThemeHelper.GetWindowsAccentColor();
            var accent = (Primary)(uint)win.ToArgb();
            var msAccent = (Accent)(uint)win.ToArgb();   // accent slot = Windows accent (shared singleton — no blue re-poison)
            _skin.ColorScheme = new ColorScheme(accent, accent, accent, msAccent, TextShade.WHITE);
        }

        private Color RowColor => ThemeHelper.IsDark ? Color.FromArgb(50, 50, 53) : Color.FromArgb(236, 236, 238);
        private Color SubText => ThemeHelper.IsDark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(110, 110, 110);

        private void RecolorRows()
        {
            foreach (var r in _rows)
            {
                r.Panel.BackColor = RowColor;
                if (r.Info != null) r.Info.ForeColor = SubText;
            }
        }

        private void BuildUi(IList<string> paths)
        {
            AutoScaleMode = AutoScaleMode.Font;
            Text = "TelegArm — Send " + paths.Count + (paths.Count == 1 ? " file" : " files");
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            FormStyle = FormStyles.ActionBar_None;
            ClientSize = new Size(520, 470);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            Sizable = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && !_sending) { DialogResult = DialogResult.Cancel; Close(); } };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12, 16, 12, 12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            _list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            foreach (var p in paths) _rows.Add(BuildRow(_rows.Count, p));
            foreach (var r in _rows) _list.Controls.Add(r.Panel);
            TelegArm.UI.Controls.TouchScroller.Enable(_list, horizontal: false);   // finger-pan the attachment list (RT touch)

            _caption = new MaterialTextBox2 { Hint = "Caption (optional)", Dock = DockStyle.Fill, Margin = new Padding(2, 8, 2, 8) };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            _sendBtn = new MaterialButton { Text = "Send", Type = MaterialButton.MaterialButtonType.Contained, AutoSize = false, Width = 110, Height = 36 };
            _sendBtn.Click += OnSend;
            _cancelBtn = new MaterialButton { Text = "Cancel", Type = MaterialButton.MaterialButtonType.Outlined, AutoSize = false, Width = 110, Height = 36 };
            _cancelBtn.Click += OnCancel;
            buttons.Controls.Add(_sendBtn);
            buttons.Controls.Add(_cancelBtn);

            layout.Controls.Add(_list, 0, 0);
            layout.Controls.Add(_caption, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout);
        }

        private Row BuildRow(int token, string path)
        {
            var row = new Row { Path = path };
            bool isImage = MediaSender.IsImage(path);
            bool isVideo = MediaSender.IsVideo(path);
            row.Toggleable = isImage || isVideo;
            row.CompressMode = isVideo ? SendMode.Video : SendMode.Photo;
            row.Mode = row.Toggleable ? row.CompressMode : SendMode.Document;

            var panel = new Panel { Width = 472, Height = 60, Margin = new Padding(0, 0, 0, 8), BackColor = RowColor };

            string kind = isImage ? "IMG" : isVideo ? "VID" : "FILE";
            var kindLabel = new MaterialLabel { Text = kind, Location = new Point(10, 8), AutoSize = false, Size = new Size(44, 22), FontType = MaterialSkinManager.fontType.Caption };

            string name = Path.GetFileName(path);
            var nameLabel = new MaterialLabel { Text = name, Location = new Point(58, 6), AutoSize = false, Size = new Size(240, 24), TextAlign = ContentAlignment.MiddleLeft };
            row.Info = new MaterialLabel { Text = DrawHelper.FormatSize(SafeSize(path)), Location = new Point(58, 30), AutoSize = false, Size = new Size(240, 20), ForeColor = SubText, FontType = MaterialSkinManager.fontType.Caption };

            row.Bar = new ProgressBar { Location = new Point(310, 34), Size = new Size(150, 12), Minimum = 0, Maximum = 100, Visible = false };

            row.ModeButton = new MaterialButton
            {
                Type = MaterialButton.MaterialButtonType.Outlined,
                AutoSize = false,
                Location = new Point(304, 12),
                Size = new Size(156, 32),
                Text = ModeText(row.Mode),
                Visible = row.Toggleable
            };
            row.ModeButton.Click += (s, e) =>
            {
                row.Mode = row.Mode == SendMode.Document ? row.CompressMode : SendMode.Document;
                row.ModeButton.Text = ModeText(row.Mode);
            };

            panel.Controls.Add(kindLabel);
            panel.Controls.Add(nameLabel);
            panel.Controls.Add(row.Info);
            panel.Controls.Add(row.Bar);
            panel.Controls.Add(row.ModeButton);
            row.Panel = panel;
            return row;
        }

        private static string ModeText(SendMode mode) => mode == SendMode.Document ? "Send as file" : "Compress";

        private void OnCancel(object sender, EventArgs e)
        {
            if (_sending) { _cts.Cancel(); _cancelBtn.Enabled = false; return; }
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private async void OnSend(object sender, EventArgs e)
        {
            if (_sending) return;
            _sending = true;
            _sendBtn.Enabled = false;
            _caption.Enabled = false;
            foreach (var r in _rows) { r.ModeButton.Enabled = false; r.Bar.Visible = true; }

            string caption = _caption.Text?.Trim() ?? "";

            // Partition into consecutive album-compatible runs (visual=photo/video vs file), chunked ≤10.
            var groups = BuildSendGroups();
            bool cancelled = false;
            for (int g = 0; g < groups.Count && !cancelled; g++)
            {
                if (_cts.IsCancellationRequested) break;
                var group = groups[g];
                string cap = (g == 0) ? caption : "";   // caption on the very first item overall

                if (group.Count == 1)
                {
                    int i = group[0];
                    var row = _rows[i];
                    FileStarting?.Invoke(i, row.Path, row.Mode, cap);
                    row.Info.Text = "Uploading…";
                    try
                    {
                        var msg = await MediaSender.SendAsync(_client, _peer, row.Path, row.Mode, cap,
                            (transmitted, total) => OnProgress(row, transmitted, total), _cts.Token);
                        row.Info.Text = "Sent"; row.Bar.Value = 100;
                        FileSucceeded?.Invoke(i, msg);
                    }
                    catch (OperationCanceledException) { row.Info.Text = "Cancelled"; FileFailed?.Invoke(i, "Cancelled"); cancelled = true; }
                    catch (Exception ex) { row.Info.Text = "Failed"; FileFailed?.Invoke(i, ex.Message); }
                }
                else
                {
                    int token0 = group[0];
                    var items = new List<MediaSender.AlbumItem>();
                    var paths = new List<string>();
                    foreach (var ix in group) { items.Add(new MediaSender.AlbumItem(_rows[ix].Path, _rows[ix].Mode)); paths.Add(_rows[ix].Path); }
                    AlbumSending?.Invoke(token0, paths, cap);
                    foreach (var ix in group) _rows[ix].Info.Text = "Uploading…";
                    try
                    {
                        var grp = group;   // capture for the progress closure
                        var msgs = await MediaSender.SendAlbumAsync(_client, _peer, items, cap,
                            k => ((transmitted, total) => OnProgress(_rows[grp[k]], transmitted, total)), _cts.Token);
                        foreach (var ix in group) { _rows[ix].Info.Text = "Sent"; _rows[ix].Bar.Value = 100; }
                        AlbumSent?.Invoke(token0, msgs);
                    }
                    catch (OperationCanceledException) { foreach (var ix in group) _rows[ix].Info.Text = "Cancelled"; AlbumFailed?.Invoke(token0, "Cancelled"); cancelled = true; }
                    catch (Exception ex) { foreach (var ix in group) _rows[ix].Info.Text = "Failed"; AlbumFailed?.Invoke(token0, ex.Message); }
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Consecutive runs of the same album class (visual photo/video vs file), each capped at 10.</summary>
        private List<List<int>> BuildSendGroups()
        {
            var groups = new List<List<int>>();
            int i = 0;
            while (i < _rows.Count)
            {
                bool visual = _rows[i].Mode != SendMode.Document;
                var grp = new List<int>();
                while (i < _rows.Count && (_rows[i].Mode != SendMode.Document) == visual && grp.Count < 10)
                {
                    grp.Add(i);
                    i++;
                }
                groups.Add(grp);
            }
            return groups;
        }

        private void OnProgress(Row row, long transmitted, long total)
        {
            if (_cts.IsCancellationRequested) throw new OperationCanceledException();
            int pct = total > 0 ? (int)(transmitted * 100 / total) : 0;
            if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (row.Bar.IsDisposed) return;
                    row.Bar.Value = pct;
                    row.Info.Text = "Uploading… " + pct + "%";
                }));
            }
            catch { /* form closing */ }
        }

        private static long SafeSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _cts.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
