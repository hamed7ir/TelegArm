using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

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
    ///
    /// Redesign step 2.2: a <see cref="DialogForm"/> (was a Material-skinned form). The file list scrolls by itself (ScrollHost) with
    /// the caption field fixed under it; Send / Cancel are the dialog's footer buttons. The upload flow (OnSend and what it
    /// raises) is unchanged. Keys: Enter = Send, Esc = Cancel — except that Esc is ignored while an upload runs (the Cancel
    /// button stops it), exactly as before.
    /// </summary>
    public class SendMediaDialog : DialogForm
    {
        // Logical (96-DPI) metrics; every use goes through Px.
        private const int RowH = 64, RowPad = 12, ModeButtonW = 132, ListMaxBody = 372, CaptionH = 60;

        private sealed class Row
        {
            public string Path;
            public SendMode Mode;
            public bool Toggleable;       // image/video can switch Compress↔File
            public SendMode CompressMode; // Photo or Video (the non-file option)
            public Panel Panel;
            public ThemedLabel KindLabel;
            public ThemedLabel NameLabel;
            public RoundedButton ModeButton;
            public UploadBar Bar;
            public ThemedLabel Info;      // shows size, then upload status
        }

        private readonly WTelegram.Client _client;
        private readonly TL.InputPeer _peer;
        private readonly List<Row> _rows = new List<Row>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _sending;
        private bool _restacking;

        private Panel _root, _captionHost;
        private NoNativeScrollPanel _list;
        private ThemedScrollBar _listBar;
        private ThemedTextBox _caption;
        private RoundedButton _sendBtn, _cancelBtn;

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
            : base("TelegArm — Send " + paths.Count + (paths.Count == 1 ? " file" : " files"), 520, 520)
        {
            _client = client;
            _peer = peer;

            BuildUi(paths);
        }

        /// <summary>Recolours what this dialog colours itself. ⚠ DialogForm's constructor calls this before ours has run, so
        /// anything built here is still null then.</summary>
        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (_root == null) return;
            var p = ThemeHelper.Palette;
            _root.BackColor = p.Window;
            _list.BackColor = p.Window;
            if (_list.Parent != null) _list.Parent.BackColor = p.Window;
            _captionHost.BackColor = p.Window;
            if (_listBar != null) { _listBar.IsDark = p.Dark; _listBar.AccentColor = p.Accent; _listBar.Invalidate(); }
            foreach (var r in _rows) { r.Panel.BackColor = p.Card; r.Panel.Invalidate(true); }
            _caption.Recolor();
        }

        private void BuildUi(IList<string> paths)
        {
            var p = ThemeHelper.Palette;
            int gap = Px(RowGap);

            _root = new Panel { BackColor = p.Window };   // UseOwnBody docks it Fill

            var listHost = new Panel { Dock = DockStyle.Fill, BackColor = p.Window };
            _list = ScrollHost.Wrap(listHost, p.Dark, p.Accent);   // themed bar + finger-pan (TouchScroller) included
            _list.BackColor = p.Window;
            var margin = _list.AutoScrollMargin; margin.Height = gap;
            _list.AutoScrollMargin = margin;                        // room under the last row
            _listBar = listHost.Controls.OfType<ThemedScrollBar>().FirstOrDefault();
            foreach (var path in paths) _rows.Add(BuildRow(_rows.Count, path));
            foreach (var r in _rows) _list.Controls.Add(r.Panel);
            _list.ClientSizeChanged += (s, e) => RestackRows();

            _captionHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Px(CaptionH),
                Padding = new Padding(Px(14), Px(10), Px(14), Px(10)),
                BackColor = p.Window,
            };
            _caption = new ThemedTextBox(false) { Hint = "Caption (optional)", Dock = DockStyle.Fill, AccessibleName = "Caption" };
            _captionHost.Controls.Add(_caption);

            _root.Controls.Add(listHost);       // Fill first: docking takes the last-added (the caption) first
            _root.Controls.Add(_captionHost);
            UseOwnBody(_root);

            _sendBtn = AddFooterButton("Send", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            _sendBtn.Click += OnSend;
            _cancelBtn = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.None);
            _cancelBtn.Click += OnCancel;
            AcceptButton = _sendBtn;   // NOT CancelButton: that would give the button DialogResult.Cancel and close the dialog mid-upload (Esc: ProcessDialogKey)
            FocusOnShow(_caption.Inner);

            // Height from the number of files (a few files = a small dialog), capped: more rows scroll in the list.
            int rowsH = _rows.Count * (Px(RowH) + gap) + gap;
            int bodyH = Math.Min(Px(ListMaxBody), rowsH) + Px(CaptionH);
            Size = new Size(Width, Bar.Height + bodyH + Px(FooterHeight) + 1);
        }

        private Row BuildRow(int token, string path)
        {
            var row = new Row { Path = path };
            bool isImage = MediaSender.IsImage(path);
            bool isVideo = MediaSender.IsVideo(path);
            row.Toggleable = isImage || isVideo;
            row.CompressMode = isVideo ? SendMode.Video : SendMode.Photo;
            row.Mode = row.Toggleable ? row.CompressMode : SendMode.Document;

            var panel = new RowPanel { Width = Px(480), Height = Px(RowH), BackColor = ThemeHelper.Palette.Card };

            string kind = isImage ? "IMG" : isVideo ? "VID" : "FILE";
            row.KindLabel = new ThemedLabel { Text = kind, Role = LabelRole.Accent, Style = FontStyle.Bold, PointSize = TextRoles.Points(TextRole.Field) };

            string name = Path.GetFileName(path);
            row.NameLabel = new ThemedLabel { Text = name, Ellipsis = true, PointSize = TextRoles.Points(TextRole.Body) };
            row.Info = new ThemedLabel { Text = DrawHelper.FormatSize(SafeSize(path)), Role = LabelRole.Dim, PointSize = TextRoles.Points(TextRole.Field) };

            row.Bar = new UploadBar { Visible = false };

            row.ModeButton = new RoundedButton
            {
                Kind = RoundedButtonKind.Secondary,
                Modern = true,
                Text = ModeText(row.Mode),
                Visible = row.Toggleable,
                Font = TextRoles.Font(TextRole.Button, WindowDpiValue),
            };
            row.ModeButton.Click += (s, e) =>
            {
                row.Mode = row.Mode == SendMode.Document ? row.CompressMode : SendMode.Document;
                row.ModeButton.Text = ModeText(row.Mode);
            };

            panel.Controls.Add(row.KindLabel);
            panel.Controls.Add(row.NameLabel);
            panel.Controls.Add(row.Info);
            panel.Controls.Add(row.Bar);
            panel.Controls.Add(row.ModeButton);
            row.Panel = panel;
            panel.SizeChanged += (s, e) => LayoutRow(row);
            LayoutRow(row);
            return row;
        }

        /// <summary>Places a row's parts from its own width: the kind tag and the name / status text on the left, the mode
        /// button on the right, the progress bar along the bottom. Own flag (Toggleable), never Control.Visible.</summary>
        private void LayoutRow(Row r)
        {
            int w = r.Panel.Width, h = r.Panel.Height;
            int pad = Px(RowPad), btnW = Px(ModeButtonW), btnH = Px(32);
            int right = w - pad;
            int textLeft = pad + Px(46);
            int textRight = r.Toggleable ? right - btnW - Px(10) : right;
            int textW = Math.Max(Px(20), textRight - textLeft);
            r.KindLabel.SetBounds(pad, Px(10), Px(40), Px(20));
            r.NameLabel.SetBounds(textLeft, Px(8), textW, Px(22));
            r.Info.SetBounds(textLeft, Px(30), textW, Px(20));
            r.ModeButton.SetBounds(right - btnW, Px(10), btnW, btnH);
            r.Bar.SetBounds(pad, h - Px(10), Math.Max(1, w - 2 * pad), Px(5));
        }

        /// <summary>Stacks the rows top-down at the list's width (the list is a plain scrolling panel, as DialogForm's body).</summary>
        private void RestackRows()
        {
            if (_list == null || _restacking) return;
            _restacking = true;
            try
            {
                int gap = Px(RowGap);
                int w = _list.ClientSize.Width - 2 * gap, y = gap + _list.AutoScrollPosition.Y;
                foreach (var r in _rows)
                {
                    if (w > Px(10) && r.Panel.Width != w) r.Panel.Width = w;
                    if (r.Panel.Left != gap) r.Panel.Left = gap;
                    if (r.Panel.Top != y) r.Panel.Top = y;
                    y += r.Panel.Height + gap;
                }
            }
            finally { _restacking = false; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RestackRows();   // the list had no width when the rows were added
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            RestackRows();
        }

        /// <summary>Esc cancels the dialog, but is ignored while an upload runs (the Cancel button stops the upload) — the
        /// old KeyDown handler's behaviour. Handled here, not through CancelButton: that would stamp DialogResult.Cancel on
        /// the Cancel button, which would then close the dialog in the middle of an upload.</summary>
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (!_sending) { DialogResult = DialogResult.Cancel; Close(); }
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        /// <summary>The title bar's ✕ (and Alt+F4) did not exist in the old dialog; during an upload they now do what the Cancel
        /// button does — stop the upload — instead of closing the dialog under it. The send flow closes the dialog itself when
        /// it unwinds, always with DialogResult.OK set just before its Close(), which is what lets that close through.
        /// Before Send, ✕ closes the dialog (Cancel) as usual. Only the FIRST close request during an upload is turned into a
        /// cancel: once cancellation has been asked for (✕, the Cancel button) a further ✕ / Alt+F4 closes the dialog, as
        /// Alt+F4 always did — otherwise an upload that never observes its token (a stalled connection, or an event handler
        /// that threw out of OnSend) would leave a modal dialog nothing can dismiss.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_sending && !_cts.IsCancellationRequested && e.CloseReason == CloseReason.UserClosing && DialogResult != DialogResult.OK)
            {
                e.Cancel = true;
                OnCancel(this, EventArgs.Empty);
                return;
            }
            base.OnFormClosing(e);
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
            _caption.InputEnabled = false;   // ThemedTextBox: never Enabled = false on it
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

        // ── small owner-drawn parts ──────────────────────────────────────────────────────────────────────────

        /// <summary>One file's card: the Card colour with a Divider hairline. Children clear to its BackColor (opaque).</summary>
        private sealed class RowPanel : Panel
        {
            public RowPanel()
            {
                ResizeRedraw = true;
                DoubleBuffered = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(ThemeHelper.Palette.Divider)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        /// <summary>The upload progress bar (replaces the native ProgressBar, whose green fill and light track ignore the
        /// theme): a Divider track with an Accent fill, 0..100, painted from the cached palette. <see cref="Value"/> is
        /// clamped like the native bar's range, so the send flow's <c>Bar.Value = pct</c> lines are unchanged.</summary>
        private sealed class UploadBar : Control
        {
            private int _value;

            public UploadBar()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }

            public int Value
            {
                get { return _value; }
                set
                {
                    value = Math.Max(0, Math.Min(100, value));
                    if (value == _value) return;
                    _value = value;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                g.Clear(Parent != null ? Parent.BackColor : p.Window);
                if (Width < 2 || Height < 2) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var track = ClientRectangle;
                track.Width -= 1; track.Height -= 1;
                using (var path = DrawHelper.RoundedRect(track, track.Height / 2))
                using (var b = new SolidBrush(p.Divider)) g.FillPath(b, path);
                int fw = (int)((long)track.Width * _value / 100);
                if (fw < 1) return;
                var fill = track;
                fill.Width = fw;
                using (var path = DrawHelper.RoundedRect(fill, fill.Height / 2))
                using (var b = new SolidBrush(p.Accent)) g.FillPath(b, path);
            }
        }
    }
}
