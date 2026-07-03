using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Full-media viewer: photos (zoom/pan) and embedded video/gif playback.
    /// Follows the app theme (System/Light/Dark) via <see cref="ThemeHelper"/>.
    /// The video control bar lives in the form (a sibling of the video surface) so
    /// the native VLC window can never paint over it.
    /// </summary>
    public class MediaViewerForm : MaterialForm
    {
        private readonly List<MediaItem> _items;
        private readonly TelegramService _service;
        private Color _accent;
        private int _index;

        private TableLayoutPanel _formLayout;
        private Control _toolbar, _content;
        private MaterialLabel _fileLabel;
        private MaterialButton _saveButton, _saveAsButton;
        private ZoomableImagePanel _imagePanel;
        private VlcVideoControl _videoControl;
        private Panel _stubPanel;
        private MaterialLabel _stubLabel;
        private Panel _docPanel;
        private Button _docOpenBtn;

        // Bottom bars (one row, toggled): photo nav vs. video controls.
        private Panel _navBar;
        private MaterialLabel _navLabel;
        private MediaControlButton _navPrev, _navNext;
        private VideoControlBar _videoBar;
        private System.Windows.Forms.Timer _playTimer;

        private Image _currentImage;

        // Theme palette (recomputed in ApplyTheme).
        private Color _bgColor, _barColor, _textColor, _subTextColor;

        public MediaViewerForm(List<MediaItem> items, int index, TelegramService service)
        {
            _items = items ?? new List<MediaItem>();
            _index = Math.Max(0, Math.Min(index, _items.Count - 1));
            _service = service;

            MaterialSkinManager.Instance.AddFormToManage(this);

            BuildUi();
            ApplyTheme();
            ShowCurrent();

            ThemeHelper.ThemeChanged += OnThemeChanged;
            FormClosed += (s, e) =>
            {
                ThemeHelper.ThemeChanged -= OnThemeChanged;
                _playTimer?.Stop();
                _videoControl.StopPlayback();
                DisposeCurrentImage();
            };
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)ApplyTheme); } catch { }
        }

        /// <summary>Recomputes the palette from <see cref="ThemeHelper"/> and re-colors the UI.</summary>
        private void ApplyTheme()
        {
            bool dark = ThemeHelper.IsDark;
            _accent = ThemeHelper.GetWindowsAccentColor();

            _bgColor = dark ? Color.FromArgb(18, 18, 18) : Color.FromArgb(245, 245, 245);
            _barColor = dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(232, 232, 234);
            _textColor = dark ? Color.White : Color.FromArgb(30, 30, 30);
            _subTextColor = dark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(110, 110, 110);

            var skin = MaterialSkinManager.Instance;
            skin.Theme = dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
            var primary = (Primary)(uint)_accent.ToArgb();
            var msAccent = (Accent)(uint)_accent.ToArgb();   // accent slot = Windows accent (shared singleton — no blue re-poison)
            skin.ColorScheme = new ColorScheme(primary, primary, primary, msAccent, TextShade.WHITE);

            if (_toolbar != null) _toolbar.BackColor = _barColor;
            if (_content != null) _content.BackColor = _bgColor;
            if (_fileLabel != null) _fileLabel.ForeColor = _textColor;
            if (_stubLabel != null) _stubLabel.ForeColor = _textColor;
            if (_docPanel != null) _docPanel.BackColor = _bgColor;
            if (_navBar != null) _navBar.BackColor = _barColor;
            if (_navLabel != null) _navLabel.ForeColor = _textColor;
            if (_navPrev != null) _navPrev.BackColor = _barColor;
            if (_navNext != null) _navNext.BackColor = _barColor;
            if (_videoBar != null) { _videoBar.AccentColor = _accent; _videoBar.IsDark = dark; }
            if (_docOpenBtn != null) { _docOpenBtn.BackColor = _accent; _docOpenBtn.ForeColor = Color.White; }

            Invalidate(true);
            _docPanel?.Invalidate();
        }

        private void BuildUi()
        {
            AutoScaleMode = AutoScaleMode.Font;
            Text = "TelegArm — Media";
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            ClientSize = new Size(900, 680);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;

            _formLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            _formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            _formLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _formLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); // bottom bar (toggled)

            _formLayout.Controls.Add(BuildToolbar(), 0, 0);
            _formLayout.Controls.Add(BuildContent(), 0, 1);

            // Bottom row holds both bars (overlapping); only one is visible at a time.
            _navBar = BuildNavBar();
            _videoBar = BuildVideoBar();
            _formLayout.Controls.Add(_navBar, 0, 2);
            _formLayout.Controls.Add(_videoBar, 0, 2);

            _playTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _playTimer.Tick += (s, e) => UpdateVideoBar();

            Controls.Add(_formLayout);
        }

        private Control BuildToolbar()
        {
            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0)
            };
            _toolbar = toolbar;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));

            _fileLabel = new MaterialLabel
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                AutoEllipsis = true
            };
            _saveAsButton = new MaterialButton { Text = "Save As…", Dock = DockStyle.Fill, Margin = new Padding(2, 7, 2, 7), Type = MaterialButton.MaterialButtonType.Text };
            _saveAsButton.Click += (s, e) => SaveAsCurrent();
            _saveButton = new MaterialButton { Text = "Save", Dock = DockStyle.Fill, Margin = new Padding(2, 7, 2, 7), Type = MaterialButton.MaterialButtonType.Text };
            _saveButton.Click += (s, e) => SaveCurrent();
            var closeButton = new MaterialButton { Text = "✕", Dock = DockStyle.Fill, Margin = new Padding(2, 7, 8, 7), Type = MaterialButton.MaterialButtonType.Text };
            closeButton.Click += (s, e) => Close();

            toolbar.Controls.Add(_fileLabel, 0, 0);
            toolbar.Controls.Add(_saveAsButton, 1, 0);
            toolbar.Controls.Add(_saveButton, 2, 0);
            toolbar.Controls.Add(closeButton, 3, 0);
            return toolbar;
        }

        private Control BuildContent()
        {
            var content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _content = content;

            _imagePanel = new ZoomableImagePanel { Dock = DockStyle.Fill, Visible = false };
            _videoControl = new VlcVideoControl { Dock = DockStyle.Fill, Visible = false };
            _videoControl.Resize += (s, e) => ApplyRoundClip();   // keep the round-video circle centered on resize
            _stubPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            _stubLabel = new MaterialLabel { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            _stubPanel.Controls.Add(_stubLabel);

            _docPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            _docPanel.Paint += PaintDoc;
            _docPanel.Resize += (s, e) => _docPanel.Invalidate();
            _docOpenBtn = new Button
            {
                Text = "Open",
                Dock = DockStyle.Bottom,
                Height = 46,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                Margin = new Padding(0)
            };
            _docOpenBtn.FlatAppearance.BorderSize = 0;
            _docOpenBtn.Click += (s, e) => OpenDocument();
            _docPanel.Controls.Add(_docOpenBtn);

            content.Controls.Add(_imagePanel);
            content.Controls.Add(_videoControl);
            content.Controls.Add(_docPanel);
            content.Controls.Add(_stubPanel);
            return content;
        }

        private Panel BuildNavBar()
        {
            var bar = new Panel { Dock = DockStyle.Fill, Visible = false };
            _navPrev = new MediaControlButton { Icon = MediaButtonIcon.Previous, Anchor = AnchorStyles.None };
            _navNext = new MediaControlButton { Icon = MediaButtonIcon.Next, Anchor = AnchorStyles.None };
            _navLabel = new MaterialLabel { TextAlign = ContentAlignment.MiddleCenter, AutoSize = false, Width = 110, Height = 36 };
            _navPrev.Click += (s, e) => Navigate(-1);
            _navNext.Click += (s, e) => Navigate(+1);

            var row = new FlowLayoutPanel { Anchor = AnchorStyles.None, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            row.Controls.Add(_navPrev);
            row.Controls.Add(_navLabel);
            row.Controls.Add(_navNext);
            bar.Controls.Add(row);
            bar.Resize += (s, e) => { row.Left = (bar.Width - row.Width) / 2; row.Top = (bar.Height - row.Height) / 2; };
            return bar;
        }

        private VideoControlBar BuildVideoBar()
        {
            // Owner-painted bar styled like MiniPlayerBar (accent circles + drawn seek).
            // It lives in the bottom TableLayoutPanel row, below — not over — the VLC
            // surface; the host's 200ms timer keeps it invalidated over VLC repaints.
            var bar = new VideoControlBar(_videoControl) { Dock = DockStyle.Fill, Visible = false };
            bar.PrevRequested += () => Navigate(-1);
            bar.NextRequested += () => Navigate(+1);
            return bar;
        }

        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowCurrent()
        {
            var item = _items[_index];
            _fileLabel.Text = item.FileName ?? item.Type;
            if (_navLabel != null) _navLabel.Text = (_index + 1) + " of " + _items.Count;

            DisposeCurrentImage();
            _playTimer.Stop();
            _videoControl.StopPlayback();
            _videoControl.Visible = false;
            _docPanel.Visible = false;

            bool isVideo = item.Type == "video" || item.Type == "gif";
            bool hasNav = _items.Count > 1;
            bool isPhoto = item.Type == "photo";

            if (isPhoto && item.Bytes != null)
                _currentImage = DecodeImage(item.Bytes);

            if (_currentImage != null)
            {
                _imagePanel.Image = _currentImage;
                _imagePanel.Visible = true;
                _stubPanel.Visible = false;
            }
            else if (isPhoto)
            {
                // Full image not downloaded yet → fetch it on demand.
                _imagePanel.Visible = false;
                _stubLabel.Text = "Loading photo…";
                _stubPanel.Visible = true;
                StartPhoto(item);
            }
            else if (isVideo)
            {
                _imagePanel.Visible = false;
                _stubPanel.Visible = false;
                _roundClip = item.IsRound;     // round video note → clip the surface to a circle
                _videoControl.Visible = true;
                ApplyRoundClip();
                StartVideo(item);
            }
            else if (item.Type == "document")
            {
                _imagePanel.Visible = false;
                _stubPanel.Visible = false;
                _docOpenBtn.Text = "Open";
                _docOpenBtn.Enabled = true;
                _docPanel.Visible = true;
                _docPanel.BringToFront();
                _docPanel.Invalidate();
            }
            else
            {
                _stubLabel.Text = StubText(item);
                _stubPanel.Visible = true;
                _imagePanel.Visible = false;
            }

            // Bottom bar: video controls for video (if VLC ready), else photo nav.
            bool showVideoBar = isVideo && _videoControl.IsReady;
            _videoBar.CanPrev = _index > 0;
            _videoBar.CanNext = _index < _items.Count - 1;
            _videoBar.Visible = showVideoBar;
            _navBar.Visible = !showVideoBar && hasNav;
            _formLayout.RowStyles[2].Height = (showVideoBar || _navBar.Visible) ? 48 : 0;
            if (showVideoBar) _playTimer.Start();

            bool hasData = item.Bytes != null || !string.IsNullOrEmpty(item.LocalPath);
            _saveButton.Enabled = hasData;
            _saveAsButton.Enabled = hasData;
        }

        private bool _roundClip;

        /// <summary>Clips the video surface to a centered circle for a round "video note" (else no clip).</summary>
        private void ApplyRoundClip()
        {
            try
            {
                if (_videoControl == null) return;
                if (!_roundClip) { if (_videoControl.Region != null) { _videoControl.Region.Dispose(); _videoControl.Region = null; } return; }
                int w = _videoControl.ClientSize.Width, h = _videoControl.ClientSize.Height;
                if (w <= 0 || h <= 0) return;
                int d = Math.Min(w, h);
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddEllipse((w - d) / 2, (h - d) / 2, d, d);
                    var old = _videoControl.Region;
                    _videoControl.Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        private void UpdateVideoBar()
        {
            if (!_videoControl.IsReady) return;
            _videoControl.RefreshOverlay();
            // The bar reads live state (time/position/playing) in its own OnPaint;
            // repaint it each tick so it also redraws over any VLC surface overdraw.
            _videoBar.Invalidate();
        }

        private void PaintDoc(object sender, PaintEventArgs e)
        {
            var item = _items[_index];
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_bgColor);

            int cx = _docPanel.Width / 2;
            int cy = _docPanel.Height / 2 - 60;
            var iconRect = new Rectangle(cx - 48, cy - 48, 96, 96);
            DrawHelper.DrawFileIcon(g, iconRect, item.FileName);

            using (var nf = new Font("Segoe UI", 12f, FontStyle.Bold))
            using (var wb = new SolidBrush(_textColor))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(item.FileName ?? "File", nf, wb, new RectangleF(20, iconRect.Bottom + 14, _docPanel.Width - 40, 28), sf);

            using (var f = new Font("Segoe UI", 9f))
            using (var gb = new SolidBrush(_subTextColor))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString(DrawHelper.FormatSize(item.FileSize), f, gb, new RectangleF(20, iconRect.Bottom + 46, _docPanel.Width - 40, 22), sf);
        }

        private async void OpenDocument()
        {
            var item = _items[_index];
            var doc = (item.RawMedia as MessageMediaDocument)?.document as Document;
            if (doc == null) return;

            string path = MediaCache.MediaPath(MediaCache.CacheFileName("document", item.Id, item.FileName));
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    _docOpenBtn.Enabled = false;
                    _docOpenBtn.Text = "Downloading…";
                    await _service.DownloadDocumentToFileAsync(doc, path);
                    if (IsDisposed) return;
                }
                item.LocalPath = path;
                _saveButton.Enabled = true;
                _saveAsButton.Enabled = true;
                _docOpenBtn.Enabled = true;
                _docOpenBtn.Text = "Open";
                Process.Start(path);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _docOpenBtn.Enabled = true;
                _docOpenBtn.Text = "Open";
                MessageBox.Show(this, "Open failed: " + ex.Message, "TelegArm", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void StartPhoto(MediaItem item)
        {
            var photo = (item.RawMedia as MessageMediaPhoto)?.photo as Photo;
            if (photo == null) { _stubLabel.Text = "Photo unavailable."; return; }
            try
            {
                var result = await _service.DownloadPhotoAsync(photo);
                if (IsDisposed || _index >= _items.Count || _items[_index] != item) return;
                if (result.bytes == null || result.bytes.Length == 0) { _stubLabel.Text = "Download failed."; return; }

                item.Bytes = result.bytes;
                if (!string.IsNullOrEmpty(result.cachePath)) item.LocalPath = result.cachePath;
                DisposeCurrentImage();
                _currentImage = DecodeImage(result.bytes);
                _imagePanel.Image = _currentImage;
                _imagePanel.Visible = true;
                _stubPanel.Visible = false;
                _saveButton.Enabled = true;
                _saveAsButton.Enabled = true;
            }
            catch (Exception ex)
            {
                if (!IsDisposed) _stubLabel.Text = "Download failed: " + ex.Message;
            }
        }

        private async void StartVideo(MediaItem item)
        {
            var doc = (item.RawMedia as MessageMediaDocument)?.document as Document;
            string path = MediaCache.MediaPath(MediaCache.CacheFileName(item.Type, item.Id, item.FileName));
            item.LocalPath = path; // enables Save once present

            try
            {
                bool complete = doc != null
                    ? TelegramService.IsFileComplete(path, doc.size)
                    : (File.Exists(path) && new FileInfo(path).Length > 0);
                if (!complete)
                {
                    if (doc == null) { _videoControl.ShowStatus("This item can't be played."); return; }

                    // DOWNLOAD-UX v3 2.4: the viewer JOINS the managed transfer (dedup registry — never a
                    // second writer): panel row, pausable elsewhere, survives the viewer closing. This is
                    // the ONE sanctioned auto-play-on-complete — the user is literally watching it.
                    _videoControl.ShowStatus("Downloading… 0%");
                    var h = _service.StartDocumentDownload(doc, path, null, item.FileName, null,
                                                           track: true, panelVisible: true, type: item.Type);
                    if (h == null) { _videoControl.ShowStatus("Download unavailable."); return; }
                    Action<Core.DownloadHandle> onProgress = hh =>
                    {
                        if (IsDisposed) return;
                        int pct = hh.Total > 0 ? (int)(hh.Transmitted * 100 / hh.Total) : 0;
                        string txt = hh.State == Core.DownloadHandle.DState.Paused
                            ? "Paused at " + pct + "%" : "Downloading… " + pct + "%";
                        try { BeginInvoke((Action)(() => { if (!IsDisposed) _videoControl.ShowStatus(txt); })); } catch { }
                    };
                    h.Changed += onProgress;
                    try { await AwaitTerminal(h); }
                    finally { h.Changed -= onProgress; }   // E-3: the viewer may close mid-transfer
                    if (h.State != Core.DownloadHandle.DState.Done)
                    {
                        if (!IsDisposed) _videoControl.ShowStatus(h.State == Core.DownloadHandle.DState.Cancelled
                            ? "Download cancelled." : "Download failed — tap to retry.");
                        return;
                    }
                }

                if (IsDisposed || _index >= _items.Count || _items[_index] != item) return;
                _videoControl.PlayFile(path, loop: item.Type == "gif", ensureAudio: item.IsRound);   // GIFs loop; round → force audio on
                _saveButton.Enabled = true;
                _saveAsButton.Enabled = true;
            }
            catch (Exception ex)
            {
                if (!IsDisposed) _videoControl.ShowStatus("Download failed: " + ex.Message);
            }
        }

        /// <summary>Awaits a handle's TERMINAL state (its Completion task; pause keeps it pending, which is
        /// right here — a paused join simply waits, showing "Paused at n%").</summary>
        private static System.Threading.Tasks.Task AwaitTerminal(Core.DownloadHandle h) { return h.Completion; }

        private static string StubText(MediaItem item)
        {
            string size = item.FileSize > 0 ? " — " + (item.FileSize / 1024.0 / 1024.0).ToString("0.0") + " MB" : "";
            string kind = char.ToUpper(item.Type[0]) + item.Type.Substring(1);
            return kind + size + "\n\n" + (item.FileName ?? "") +
                   "\n\nPreview for this type is coming in the next phase.";
        }

        private static Image DecodeImage(byte[] bytes)
        {
            try
            {
                using (var src = new MemoryStream(bytes))
                using (var tmp = Image.FromStream(src))
                    return new Bitmap(tmp);
            }
            catch { return null; }
        }

        private void Navigate(int delta)
        {
            int next = _index + delta;
            if (next < 0 || next >= _items.Count) return;
            _index = next;
            ShowCurrent();
        }

        private void SaveCurrent()
        {
            var item = _items[_index];
            try
            {
                string folder = AppSettings.Instance.DefaultSaveFolder;
                MediaCache.EnsureFolder(folder);
                string target = Path.Combine(folder, SafeName(item.FileName ?? ("media_" + item.Id)));
                if (!WriteItem(item, target)) return;
                MessageBox.Show(this, "Saved to:\n" + target, "TelegArm", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "TelegArm", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveAsCurrent()
        {
            var item = _items[_index];
            using (var dlg = new SaveFileDialog
            {
                FileName = SafeName(item.FileName ?? ("media_" + item.Id)),
                Filter = FilterFor(item.Type),
                InitialDirectory = AppSettings.Instance.DefaultSaveFolder
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (!WriteItem(item, dlg.FileName)) return;
                    MessageBox.Show(this, "Saved to:\n" + dlg.FileName, "TelegArm", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Save failed: " + ex.Message, "TelegArm", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // Save/filter/name logic is shared with the profile gallery via Core/MediaSaver.
        private static bool WriteItem(MediaItem item, string target) => MediaSaver.Write(item, target);
        private static string FilterFor(string type) => MediaSaver.FilterFor(type);
        private static string SafeName(string name) => MediaSaver.SafeName(name);

        private void DisposeCurrentImage()
        {
            _imagePanel.Image = null;
            _currentImage?.Dispose();
            _currentImage = null;
        }
    }
}
