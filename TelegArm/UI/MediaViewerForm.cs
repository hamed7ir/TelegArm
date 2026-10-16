using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
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
    ///
    /// Redesign step 2.3: an <see cref="AppForm"/> (was a MaterialForm). The title bar is TelegArm's own (✕ replaces the
    /// old "✕" font-glyph button); the toolbar (file name, Save As, Save) and the bottom bar are DOCKED rows around the
    /// content — never overlaid on the VLC child window, which paints over anything above it. 900×680 logical by default,
    /// 560×420 minimum. Colours from the palette; errors and confirmations in ThemedDialog (was MessageBox).
    /// </summary>
    public class MediaViewerForm : AppForm
    {
        private readonly List<MediaItem> _items;
        private readonly TelegramService _service;
        private int _index;

        private Panel _toolbar, _content, _bottom;
        private ThemedLabel _fileLabel;
        private RoundedButton _saveButton, _saveAsButton;
        private ZoomableImagePanel _imagePanel;
        private VideoPlayerControl _videoControl;
        private Panel _stubPanel;
        private ThemedLabel _stubLabel;
        private Panel _docPanel;
        private RoundedButton _docOpenBtn;

        // Bottom bars (one row, toggled): photo nav vs. video controls.
        private Panel _navBar;
        private ThemedLabel _navLabel;
        private MediaControlButton _navPrev, _navNext;
        private VideoControlBar _videoBar;
        private System.Windows.Forms.Timer _playTimer;

        private Image _currentImage;

        private const int BarHeight = 48;

        public MediaViewerForm(List<MediaItem> items, int index, TelegramService service)
        {
            _items = items ?? new List<MediaItem>();
            _index = Math.Max(0, Math.Min(index, _items.Count - 1));
            _service = service;

            BuildUi();
            ApplyTheme();
            ShowCurrent();

            FormClosed += (s, e) =>
            {
                _playTimer?.Stop();
                _videoControl.StopPlayback();
                DisposeCurrentImage();
            };
        }

        /// <summary>AppForm raises this on every theme change (it subscribes and unsubscribes ThemeHelper itself).</summary>
        protected override void OnThemeChanged() { ApplyTheme(); }

        /// <summary>Re-colours the UI from the palette.</summary>
        private void ApplyTheme()
        {
            var p = ThemeHelper.Palette;
            Color bg = p.SurfaceAlt, bar = p.Footer;

            if (_toolbar != null) _toolbar.BackColor = bar;
            if (_content != null) _content.BackColor = bg;
            if (_bottom != null) _bottom.BackColor = bar;
            if (_docPanel != null) _docPanel.BackColor = bg;
            if (_stubPanel != null) _stubPanel.BackColor = bg;
            if (_navBar != null) _navBar.BackColor = bar;
            if (_navPrev != null) _navPrev.BackColor = bar;
            if (_navNext != null) _navNext.BackColor = bar;
            if (_videoBar != null) { _videoBar.AccentColor = p.Accent; _videoBar.IsDark = p.Dark; }

            Invalidate(true);
            _docPanel?.Invalidate();
        }

        private void BuildUi()
        {
            Text = "TelegArm — Media";
            Size = new Size(Px(900), Px(680));
            LogicalMinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;

            // Docked, not a TableLayoutPanel: DpiScaler scales docked heights, so the rows follow a monitor move.
            _bottom = new Panel { Dock = DockStyle.Bottom, Height = Px(BarHeight), Visible = false };
            _navBar = BuildNavBar();
            _videoBar = BuildVideoBar();
            _bottom.Controls.Add(_navBar);
            _bottom.Controls.Add(_videoBar);

            _playTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _playTimer.Tick += (s, e) => UpdateVideoBar();

            Content.Controls.Add(BuildContent());   // Fill first: docking takes the last-added first
            Content.Controls.Add(_bottom);
            Content.Controls.Add(BuildToolbar());
        }

        private Control BuildToolbar()
        {
            var toolbar = new Panel { Dock = DockStyle.Top, Height = Px(BarHeight) };
            _toolbar = toolbar;

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Padding = new Padding(0, Px(6), Px(8), Px(6)),
            };
            _saveButton = new RoundedButton { Text = "Save", Kind = RoundedButtonKind.Neutral, Modern = true, Width = Px(96), Height = Px(36), Margin = new Padding(Px(6), 0, 0, 0), Font = TextRoles.Font(TextRole.Button, WindowDpiValue) };
            _saveButton.Click += (s, e) => SaveCurrent();
            _saveAsButton = new RoundedButton { Text = "Save As…", Kind = RoundedButtonKind.Neutral, Modern = true, Width = Px(116), Height = Px(36), Margin = new Padding(Px(6), 0, 0, 0), Font = TextRoles.Font(TextRole.Button, WindowDpiValue) };
            _saveAsButton.Click += (s, e) => SaveAsCurrent();
            buttons.Controls.Add(_saveButton);     // RightToLeft: first added = rightmost
            buttons.Controls.Add(_saveAsButton);

            _fileLabel = new ThemedLabel { Dock = DockStyle.Fill, Ellipsis = true, Role = LabelRole.Body, Padding = new Padding(Px(14), 0, 0, 0) };

            toolbar.Controls.Add(_fileLabel);   // Fill first
            toolbar.Controls.Add(buttons);
            return toolbar;
        }

        private Control BuildContent()
        {
            var content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _content = content;

            _imagePanel = new ZoomableImagePanel { Dock = DockStyle.Fill, Visible = false };
            _videoControl = new VideoPlayerControl { Dock = DockStyle.Fill, Visible = false };
            _videoControl.Resize += (s, e) => ApplyRoundClip();   // keep the round-video circle centered on resize
            _stubPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            _stubLabel = new ThemedLabel { Dock = DockStyle.Fill, Wrap = true, TextAlign = HorizontalAlignment.Center, Role = LabelRole.Body, Padding = new Padding(Px(24)) };
            _stubPanel.Controls.Add(_stubLabel);

            _docPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            _docPanel.Paint += PaintDoc;
            _docPanel.Resize += (s, e) => { PlaceDocButton(); _docPanel.Invalidate(); };
            // Step 2.3: a button under the file's name (Telegram's layout), not 1.x's full-width bar docked at the bottom.
            _docOpenBtn = new RoundedButton
            {
                Text = "Open",
                Size = new Size(Px(180), Px(42)),
                Kind = RoundedButtonKind.Primary,
                Modern = true,
                Margin = new Padding(0),
                Font = TextRoles.Font(TextRole.Button, WindowDpiValue),
            };
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
            _navPrev = new MediaControlButton { Icon = MediaButtonIcon.Previous, Size = new Size(Px(36), Px(36)), AccessibleName = "Previous" };
            _navNext = new MediaControlButton { Icon = MediaButtonIcon.Next, Size = new Size(Px(36), Px(36)), AccessibleName = "Next" };
            _navLabel = new ThemedLabel { TextAlign = HorizontalAlignment.Center, Width = Px(110), Height = Px(36), Role = LabelRole.Body };
            _navPrev.Click += (s, e) => Navigate(-1);
            _navNext.Click += (s, e) => Navigate(+1);
            bar.Controls.Add(_navPrev);
            bar.Controls.Add(_navLabel);
            bar.Controls.Add(_navNext);
            // Placed by hand, centred both ways: an AutoSize FlowLayoutPanel was centred while it still had its default
            // height, so the row sat at the top of the bar (and its items top-aligned within it).
            bar.Resize += (s, e) =>
            {
                int gap = Px(8), x = (bar.Width - _navPrev.Width - _navLabel.Width - _navNext.Width - 2 * gap) / 2, cy = bar.Height / 2;
                _navPrev.Location = new Point(x, cy - _navPrev.Height / 2);
                x += _navPrev.Width + gap;
                _navLabel.Location = new Point(x, cy - _navLabel.Height / 2);
                x += _navLabel.Width + gap;
                _navNext.Location = new Point(x, cy - _navNext.Height / 2);
            };
            return bar;
        }

        private VideoControlBar BuildVideoBar()
        {
            // Owner-painted bar styled like MiniPlayerBar (accent circles + drawn seek).
            // It lives in the docked bottom row, below — not over — the VLC
            // surface; the host's 200ms timer keeps it invalidated over VLC repaints.
            var bar = new VideoControlBar(_videoControl) { Dock = DockStyle.Fill, Visible = false };
            bar.PrevRequested += () => Navigate(-1);
            bar.NextRequested += () => Navigate(+1);
            return bar;
        }

        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            // R6: Left / Right step through the album, as ProfilePhotoViewer (nothing here takes the arrows: no text field)
            if (keyData == Keys.Left) { Navigate(-1); return true; }
            if (keyData == Keys.Right) { Navigate(+1); return true; }
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

            // Bottom bar: video controls for video (if VLC ready), else photo nav. The decision uses OUR flags, never
            // Control.Visible (false for every child while the window is not shown yet).
            bool showVideoBar = isVideo && _videoControl.IsReady;
            bool showNav = !showVideoBar && hasNav;
            _videoBar.CanPrev = _index > 0;
            _videoBar.CanNext = _index < _items.Count - 1;
            _videoBar.Visible = showVideoBar;
            _navBar.Visible = showNav;
            _bottom.Visible = showVideoBar || showNav;
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

        /// <summary>The file icon's box in the document panel; the name, size and Open button stack under it.</summary>
        private Rectangle DocIconRect()
        {
            int cx = _docPanel.Width / 2, cy = _docPanel.Height / 2 - Px(70), r = Px(48);
            return new Rectangle(cx - r, cy - r, 2 * r, 2 * r);
        }

        private void PlaceDocButton()
        {
            if (_docOpenBtn == null) return;
            var icon = DocIconRect();
            _docOpenBtn.Location = new Point((_docPanel.Width - _docOpenBtn.Width) / 2, icon.Bottom + Px(84));
        }

        private void PaintDoc(object sender, PaintEventArgs e)
        {
            var item = _items[_index];
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(p.SurfaceAlt);

            var iconRect = DocIconRect();
            DrawHelper.DrawFileIcon(g, iconRect, item.FileName);

            // Shared role fonts (never disposed) instead of 1.x's per-paint new Font(...).
            string name = item.FileName ?? "File";
            TextRenderer.DrawText(g, name, TextRoles.Font(TextRole.DialogTitle, dpi, name),
                new Rectangle(Px(20), iconRect.Bottom + Px(14), _docPanel.Width - Px(40), Px(28)), p.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, DrawHelper.FormatSize(item.FileSize), TextRoles.Font(TextRole.Field, dpi),
                new Rectangle(Px(20), iconRect.Bottom + Px(46), _docPanel.Width - Px(40), Px(22)), p.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
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
                ThemedDialog.Show(this, "Open", "Open failed: " + ex.Message, "OK");
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
                ThemedDialog.Show(this, "Saved", "Saved to:\n" + target, "OK");
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Save", "Save failed: " + ex.Message, "OK");
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
                    ThemedDialog.Show(this, "Saved", "Saved to:\n" + dlg.FileName, "OK");
                }
                catch (Exception ex)
                {
                    ThemedDialog.Show(this, "Save", "Save failed: " + ex.Message, "OK");
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
