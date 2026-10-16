using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Telegram-style media panel with three tabs: Emoji (virtualized color grid), Stickers
    /// (the user's favorite + recent stickers from the API), and GIFs (saved GIFs). Emoji raise
    /// <see cref="Picked"/>; stickers/GIFs raise <see cref="DocumentPicked"/> with the document.
    /// Sticker/GIF lists load lazily on first open.
    ///
    /// Step 3.1: every length is LOGICAL (96-DPI) through Px at the picker's own DPI; fonts are shared FontCache fonts
    /// (never disposed). A POPUP lays out at the DPI of where it opens (the caller passes Dpi.ForPopup(anchor)), is an
    /// IDpiHost so its controls read that DPI, and closes on WM_DPICHANGED. EMBEDDED it takes its host window's DPI and
    /// MainForm's rescale reaches it as IDpiAware: ApplyMetrics SETS everything from logical values, never multiplies.
    /// At 96 DPI every value is exactly the 1.x one.
    /// </summary>
    public class EmojiPicker : Form, IDpiHost, IDpiAware
    {
        private readonly TelegramService _service;
        private readonly bool _dark;
        private readonly Color _accent;
        private readonly bool _embedded;   // TA-23/D3: docked pane rather than floating popup
        private int _dpi;                  // step 3.1: the DPI this picker lays out at (popup: where it opened; embedded: its host's)

        public event Action<string> Picked;
        public event Action<Document> DocumentPicked;

        private Label _tabEmoji, _tabStickers, _tabGifs;
        private Panel _emojiPanel, _stickerPanel, _gifPanel;
        private FlowLayoutPanel _stickerFlow, _gifFlow, _stickerPackBar;
        private TextBox _stickerSearch;
        private bool _stickersLoaded, _gifsLoaded;

        // Step 3.1: what ApplyMetrics re-sizes on a DPI change.
        private Panel _tabBar, _catHost, _packHost;
        private FlowLayoutPanel _catBar;
        private Controls.NoNativeScrollPanel _emojiHost;
        private Canvas _canvas;
        private readonly List<KeyValuePair<Label, string>> _catButtons = new List<KeyValuePair<Label, string>>();   // button → its first emoji

        private int Px(int n) { return Dpi.Px(n, _dpi); }

        int IDpiHost.Dpi { get { return _dpi; } }

        // Decoded previews kept across panel opens so reopening / switching packs is instant.
        private static readonly Dictionary<long, Image> _previewCache = new Dictionary<long, Image>();

        /// <param name="embedded">BATCH-TA-23/D3 — host this panel INSIDE another control (the right-side
        /// dock) instead of as a floating popup. The CONTENT is identical either way — that is the point:
        /// the dock must show the composer's panel, not a second implementation of it, or the two drift.
        ///
        /// ⚠ WHY THE SAME CLASS IS EMBEDDED RATHER THAN ITS CONTENT EXTRACTED INTO A Panel.
        /// Extraction is the tidier end state and is still worth doing, but it means hand-moving ~450 lines
        /// of layout with no harness to catch a transcription slip, to gain nothing a user can see. Setting
        /// TopLevel = false and docking the form is a standard WinForms embed, it keeps the popup and the
        /// dock LITERALLY the same type, and it converts back into a proper extraction later without
        /// touching a single line of the content. The cost is that this class now has two modes, which is
        /// what every guard below is for.
        /// The host does: `new EmojiPicker(svc, dark, accent, embedded: true, dpi: WindowDpiValue) { TopLevel = false,
        /// Dock = DockStyle.Fill }`, adds it, then calls Show().</param>
        /// <param name="dpi">Step 3.1 — the DPI to lay out at: a popup's Dpi.ForPopup(anchor), an embedded pane's host
        /// window DPI. 0 = <see cref="Dpi.DefaultDpi"/>.</param>
        public EmojiPicker(TelegramService service, bool dark, Color accent, bool embedded = false, int dpi = 0)
        {
            _service = service; _dark = dark; _accent = accent; _embedded = embedded;
            _dpi = dpi > 0 ? dpi : Dpi.DefaultDpi;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TelegArm.Helpers.AppIcon.Set(this);   // app icon in Alt-Tab
            Size = new Size(Px(380), Px(380));   // a popup is sized at its DPI BEFORE it is shown (the caller positions by it)
            BackColor = ThemeHelper.PaletteFor(dark).EmojiBg;
            if (!embedded)
            {
                // POPUP ONLY. Embedded, Esc belongs to the chat and there is nothing to dismiss; and
                // Deactivate would tear the pane down the moment focus moved to the message input — i.e.
                // every single time the user typed.
                KeyPreview = true;
                KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
                Deactivate += (s, e) => { try { Close(); } catch { } };
            }

            BuildTabs();
            BuildEmoji();
            BuildStickerTab();
            BuildList(ref _gifPanel, ref _gifFlow);
            ShowTab(0);
        }

        // ── DPI (step 3.1) ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>EMBEDDED: MainForm's rescale reaches the pane (children first, then this). DpiScaler has already
        /// ratio-scaled bounds, Padding, Margin and explicit fonts; ApplyMetrics re-SETS them from logical values.</summary>
        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            if (newDpi <= 0) return;
            _dpi = newDpi;
            ApplyMetrics();
        }

        /// <summary>EMBEDDED: adopt the host window's DPI once parented, should it differ from the one the host passed.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (!_embedded || TopLevel || Parent == null) return;
            int d = Dpi.Of(this);
            if (d > 0 && d != _dpi) { _dpi = d; ApplyMetrics(); }
        }

        /// <summary>A POPUP does not rescale: when its monitor's DPI changes under it, it swallows WM_DPICHANGED (WinForms'
        /// own handling must not scale it) and closes; the next open is sized where it opens. Embedded it is a child
        /// window, which never receives the message.</summary>
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WindowDpi.WM_DPICHANGED && !_embedded)
            {
                m.Result = IntPtr.Zero;
                try { Close(); } catch { }
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>The emoji grid takes the scroll host's width (Px(2) short, as the 1.x 2 px).</summary>
        private void RelayoutCanvas()
        {
            if (_canvas == null || _emojiHost == null) return;
            _canvas.SetWidth(_emojiHost.ClientSize.Width - Px(2));
        }

        /// <summary>SETS every DPI-dependent length, font and image from its logical value at the current DPI — never
        /// multiplies, so it is safe after DpiScaler's generic pass and when nothing was scaled at all.</summary>
        private void ApplyMetrics()
        {
            SuspendLayout();
            try
            {
                _tabBar.Height = Px(38);
                foreach (var t in new[] { _tabEmoji, _tabStickers, _tabGifs }) t.Font = FontHelper.UiAt(10.5f, _dpi, FontStyle.Bold);
                LayoutTabs();

                _catHost.Height = Px(48);
                _catBar.SuspendLayout();
                _catBar.Padding = new Padding(Px(4), Px(3), Px(4), 0);
                foreach (var cb in _catButtons) StyleCatButton(cb.Key, cb.Value);
                _catBar.ResumeLayout(true);

                _packHost.Height = Px(52);
                _stickerPackBar.SuspendLayout();
                _stickerPackBar.Padding = new Padding(Px(4), Px(4), Px(4), 0);
                foreach (Control c in _stickerPackBar.Controls) { var l = c as Label; if (l != null) SizePackButton(l); }
                _stickerPackBar.ResumeLayout(true);
                _stickerSearch.Font = FontHelper.UiAt(9.5f, _dpi);   // shared: never dispose

                foreach (var flow in new[] { _stickerFlow, _gifFlow })
                {
                    flow.SuspendLayout();
                    flow.Padding = new Padding(Px(6));
                    foreach (Control c in flow.Controls) { var tile = c as MediaTile; if (tile != null) tile.SetDpi(_dpi); }
                    flow.ResumeLayout(true);
                }
            }
            finally { ResumeLayout(true); }
            if (_canvas != null) _canvas.SetDpi(_dpi);
            RelayoutCanvas();
        }

        private void BuildTabs()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = Px(38), BackColor = BackColor };
            _tabBar = bar;
            _tabEmoji = MakeTab("Emoji", 0);
            _tabStickers = MakeTab("Stickers", 1);
            _tabGifs = MakeTab("GIFs", 2);
            bar.Controls.Add(_tabEmoji);
            bar.Controls.Add(_tabStickers);
            bar.Controls.Add(_tabGifs);
            bar.Resize += (s, e) => LayoutTabs();
            Controls.Add(bar);
        }

        /// <summary>The three tabs in thirds of the bar.</summary>
        private void LayoutTabs()
        {
            int w = _tabBar.ClientSize.Width / 3;
            _tabEmoji.SetBounds(0, 0, w, _tabBar.Height);
            _tabStickers.SetBounds(w, 0, w, _tabBar.Height);
            _tabGifs.SetBounds(2 * w, 0, _tabBar.ClientSize.Width - 2 * w, _tabBar.Height);
        }

        private Label MakeTab(string text, int idx)
        {
            var lbl = new Label
            {
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Font = FontHelper.UiAt(10.5f, _dpi, FontStyle.Bold),   // shared: never dispose
                ForeColor = ThemeHelper.PaletteFor(_dark).EmojiDim
            };
            lbl.Click += (s, e) => ShowTab(idx);
            return lbl;
        }

        private void BuildEmoji()
        {
            _emojiPanel = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Visible = false };
            // Step 3.1 — PARENT BEFORE CHILDREN: the page joins the picker first, so the ThemedScrollBars added below
            // adopt the picker's DPI when parented. Add order inside every container is unchanged (docking keys on it).
            Controls.Add(_emojiPanel);

            // ⚠ BATCH-TA-39 — THE CATEGORY STRIP USES THE **STICKER PACK BAR'S** SHAPE, NOT A PLAIN
            //   FlowLayoutPanel. It was `new FlowLayoutPanel { AutoScroll = true }` + ScrollbarTheme.Apply,
            //   and on Windows 8.1 that showed a WHITE native scrollbar under the categories while the
            //   sticker strip beside it looked right.
            //   WHY: AutoScroll on an ordinary panel creates a NATIVE Win32 scrollbar, and
            //   ScrollbarTheme.Apply cannot repaint one — on Win10/11 the system bar happens to be dark
            //   already, which is why this only showed up on the device. NoNativeScrollFlowPanel
            //   SUPPRESSES the native bar so an owner-drawn ThemedScrollBar can be docked instead, which
            //   is exactly what BuildStickerTab does with _stickerPackBar + packHost.
            //   Same construction here, so the two strips cannot diverge again.
            var catBar = new Controls.NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true,
                BackColor = ThemeHelper.PaletteFor(_dark).EmojiTabBar, Padding = new Padding(Px(4), Px(3), Px(4), 0)
            };
            var catHost = new Panel
            {
                Dock = DockStyle.Bottom, Height = Px(48),
                BackColor = catBar.BackColor
            };
            _catBar = catBar; _catHost = catHost;

            var host = new Controls.NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
            ScrollbarTheme.Apply(host, _dark);
            var canvas = new Canvas(EmojiRenderer.Catalog(), _dark, _dpi) { BackColor = BackColor };
            _emojiHost = host; _canvas = canvas;
            // Embedded, picking must NOT dismiss the pane — the whole value of a dock is picking several
            // in a row without reopening it.
            canvas.Picked += e => { Picked?.Invoke(e); if (!_embedded) Close(); };
            host.Controls.Add(canvas);

            EventHandler relayout = (s, e) => RelayoutCanvas();
            host.Resize += relayout;
            Shown += relayout;
            // ⚠ EMBEDDED, `Shown` IS NOT ENOUGH ON ITS OWN. A docked pane is sized by its parent's layout
            //   pass, which can run before or after Show(), and if the canvas never gets a width the grid
            //   lays out at its 360 default and clips. HandleCreated gives a second, earlier trigger; both
            //   are idempotent (SetWidth → Relayout is a pure recompute).
            HandleCreated += relayout;

            // Quick-nav: one button per category that scrolls the grid to that section.
            foreach (var cat in canvas.Categories)
            {
                var b = new Label { AutoSize = false, Cursor = Cursors.Hand };
                string first = canvas.FirstEmojiOf(cat);
                if (EmojiRenderer.Get(first) == null)
                {
                    b.Text = cat.Length > 0 ? cat.Substring(0, 1) : "?";
                    b.TextAlign = ContentAlignment.MiddleCenter;
                    b.ForeColor = ThemeHelper.PaletteFor(_dark).EmojiTabText;
                }
                StyleCatButton(b, first);   // size, margin, and the icon image or the letter's font, at _dpi
                _catButtons.Add(new KeyValuePair<Label, string>(b, first));
                string catName = cat;
                b.Click += (s, e) => { host.AutoScrollPosition = new Point(0, canvas.TopOf(catName)); };
                catBar.Controls.Add(b);
            }

            _emojiPanel.Controls.Add(host);     // Fill (add first → docks last)
            _emojiPanel.Controls.Add(catHost);   // Bottom — the strip plus its owner-drawn scrollbar
            catHost.Controls.Add(catBar);   // Fill added first → docks last, taking the remainder
            catHost.Controls.Add(new Controls.ThemedScrollBar(catBar, _dark, _accent, horizontal: true) { Dock = DockStyle.Bottom });
            AddScrollbar(_emojiPanel, host);    // Right
            TelegArm.UI.Controls.TouchScroller.Enable(host, horizontal: false);      // finger-pan the emoji grid (RT touch)
            TelegArm.UI.Controls.TouchScroller.Enable(catBar, horizontal: true);     // finger-pan the category bar
        }

        private void BuildList(ref Panel panel, ref FlowLayoutPanel flow)
        {
            // Tab page = host with a themed scrollbar; the inner scroll panel holds the flow.
            var page = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Visible = false };
            Controls.Add(page);            // step 3.1: parent before children (the scrollbar adopts the picker's DPI)
            var scroll = new Controls.NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
            flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = BackColor, Padding = new Padding(Px(6)) };
            scroll.Controls.Add(flow);
            page.Controls.Add(scroll);     // Fill (add first → docks last)
            AddScrollbar(page, scroll);    // Right
            panel = page;                  // ShowTab toggles this page's visibility
            // Finger-pan the sticker/GIF grid (RT touch); register each tile as it's added so a drag STARTING
            // on a tile still pans the surface (SurfaceOf walks up to 'scroll'), and a tap picks the tile.
            TelegArm.UI.Controls.TouchScroller.Enable(scroll, horizontal: false);
            flow.ControlAdded += (s, e) => TelegArm.UI.Controls.TouchScroller.RegisterControl(e.Control);
        }

        /// <summary>Docks a themed scrollbar on the right of <paramref name="parent"/> driving <paramref name="target"/>.</summary>
        private void AddScrollbar(Panel parent, ScrollableControl target)
            => parent.Controls.Add(new Controls.ThemedScrollBar(target, _dark, _accent) { Dock = DockStyle.Right });

        private void BuildStickerTab()
        {
            _stickerPanel = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Visible = false };
            Controls.Add(_stickerPanel);   // step 3.1: parent before children (the scrollbars adopt the picker's DPI)

            _stickerPackBar = new Controls.NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true,
                BackColor = ThemeHelper.PaletteFor(_dark).EmojiTabBar, Padding = new Padding(Px(4), Px(4), Px(4), 0)
            };
            // Host the pack strip with a themed horizontal scrollbar along the bottom.
            var packHost = new Panel
            {
                Dock = DockStyle.Bottom, Height = Px(52),
                BackColor = _stickerPackBar.BackColor
            };
            _packHost = packHost;
            _stickerSearch = new TextBox
            {
                Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle,
                BackColor = ThemeHelper.PaletteFor(_dark).EmojiGrid,
                ForeColor = _dark ? Color.White : Color.Black,
                Font = FontHelper.UiAt(9.5f, _dpi)   // shared: never dispose
            };
            SetCue(_stickerSearch, "Search stickers by emoji…");
            _stickerSearch.KeyDown += async (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string q = _stickerSearch.Text.Trim();
                if (string.IsNullOrEmpty(q)) { await ShowFavedRecent(); return; }
                await SearchStickers(q);
            };

            var scrollHost = new Controls.NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
            _stickerFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = BackColor, Padding = new Padding(Px(6)) };
            scrollHost.Controls.Add(_stickerFlow);

            // Docking order is reverse of add order (last added docks first), so the Fill host
            // must be added first to receive the leftover space between the search row and pack bar.
            _stickerPanel.Controls.Add(scrollHost);        // Fill (add first → docks last)
            _stickerPanel.Controls.Add(packHost);          // Bottom (pack strip + its h-scrollbar)
            packHost.Controls.Add(_stickerPackBar);   // Fill (add first → docks last)
            packHost.Controls.Add(new Controls.ThemedScrollBar(_stickerPackBar, _dark, _accent, horizontal: true) { Dock = DockStyle.Bottom });
            _stickerPanel.Controls.Add(_stickerSearch);    // Top
            AddScrollbar(_stickerPanel, scrollHost);       // Right

            TelegArm.UI.Controls.TouchScroller.Enable(_stickerPackBar, horizontal: true);
        }

        private static void ClearFlow(FlowLayoutPanel flow)
        {
            var old = flow.Controls.Cast<Control>().ToArray();
            flow.Controls.Clear();
            foreach (var c in old) c.Dispose();
        }

        private void AddPackButton(string label, StickerSet set)
        {
            var b = new Label
            {
                Text = label,
                AutoSize = false,
                Tag = set == null ? 40 : 84,   // step 3.1: the LOGICAL width, re-applied by SizePackButton on a DPI change
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                ForeColor = ThemeHelper.PaletteFor(_dark).EmojiSearchText
            };
            SizePackButton(b);
            b.Click += async (s, e) => { if (set == null) await ShowFavedRecent(); else await LoadSet(set); };
            _stickerPackBar.Controls.Add(b);
        }

        /// <summary>Step 3.1 — a pack button's size, margin and font at the picker's DPI, SET from logical values.</summary>
        private void SizePackButton(Label b)
        {
            int w = b.Tag is int ? (int)b.Tag : 84;
            b.Size = new Size(Px(w), Px(34));
            b.Margin = new Padding(Px(2));
            b.Font = FontHelper.UiAt(9f, _dpi);   // shared: never dispose
        }

        /// <summary>Step 3.1 — a category button's size and margin, and its icon (an OWNED copy at Px(24): a GetScaled
        /// image could be disposed by step 3.5's cache trim while the label still shows it) or its letter's font.
        /// Replaces and disposes the previous icon, so it is safe to call again on a DPI change.</summary>
        private void StyleCatButton(Label b, string emoji)
        {
            b.Size = new Size(Px(34), Px(34));
            b.Margin = new Padding(Px(2));
            var icon = EmojiRenderer.Get(emoji);   // the full-size image the renderer's cache owns: copied, never kept
            if (icon != null)
            {
                var old = b.Image;
                b.Image = new Bitmap(icon, new Size(Px(24), Px(24)));
                b.ImageAlign = ContentAlignment.MiddleCenter;
                if (old != null) old.Dispose();
            }
            else b.Font = FontHelper.UiAt(10f, _dpi, FontStyle.Bold);   // shared: never dispose
        }

        private async System.Threading.Tasks.Task ShowFavedRecent()
        {
            ClearFlow(_stickerFlow);
            var seen = new HashSet<long>();
            foreach (var arr in new[] { await _service.GetFavedStickersAsync(), await _service.GetRecentStickersAsync() })
            {
                if (IsDisposed) return;
                foreach (var doc in arr)
                    if (doc != null && seen.Add(doc.id)) AddTile(_stickerFlow, doc, 72, true);
            }
        }

        private async System.Threading.Tasks.Task LoadSet(StickerSet set)
        {
            ClearFlow(_stickerFlow);
            var docs = await _service.GetStickerSetAsync(set.id, set.access_hash);
            if (IsDisposed) return;
            foreach (var doc in docs)
                if (doc != null) AddTile(_stickerFlow, doc, 72, true);
        }

        private void ShowTab(int idx)
        {
            _emojiPanel.Visible = idx == 0;
            _stickerPanel.Visible = idx == 1;
            _gifPanel.Visible = idx == 2;
            if (idx == 0) _emojiPanel.BringToFront();
            if (idx == 1) { _stickerPanel.BringToFront(); LoadStickers(); }
            if (idx == 2) { _gifPanel.BringToFront(); LoadGifs(); }

            _tabEmoji.ForeColor = TabColor(idx == 0);
            _tabStickers.ForeColor = TabColor(idx == 1);
            _tabGifs.ForeColor = TabColor(idx == 2);
        }

        private Color TabColor(bool active)
            => active ? ColorMath.Ink(_accent, BackColor, 4.5) : (ThemeHelper.PaletteFor(_dark).EmojiDim);   // accent as text: keep it readable on any accent

        private async void LoadStickers()
        {
            if (_stickersLoaded) return;
            _stickersLoaded = true;

            await ShowFavedRecent();                 // default view = favorites + recent
            if (IsDisposed) return;

            AddPackButton("★", null);                // pack bar: ★ (recent) + one per installed set
            var sets = await _service.GetStickerSetsAsync();
            if (IsDisposed) return;
            foreach (var set in sets)
            {
                string t = set.title ?? "Set";
                AddPackButton(t.Length > 12 ? t.Substring(0, 12) + "…" : t, set);
            }
        }

        private async void LoadGifs()
        {
            if (_gifsLoaded) return;
            _gifsLoaded = true;
            var gifs = await _service.GetSavedGifsAsync();
            if (IsDisposed) return;
            int n = 0;
            foreach (var doc in gifs)
            {
                if (doc == null) continue;
                AddTile(_gifFlow, doc, 96, sticker: false);
                if (++n >= 60) break;   // cap for performance
            }
        }

        private void AddTile(FlowLayoutPanel flow, Document doc, int size, bool sticker)
        {
            var tile = new MediaTile(doc, size, _dark, _dpi);   // size is LOGICAL; the tile sets its px size and margin
            tile.Click += (s, e) => { DocumentPicked?.Invoke(doc); if (!_embedded) Close(); };
            flow.Controls.Add(tile);
            LoadPreview(tile, doc, sticker);
        }

        private static string StickerCachePath(long id)
            => MediaCache.ThumbPath("sticker_" + id + ".png");

        private async void LoadPreview(MediaTile tile, Document doc, bool sticker)
        {
            try
            {
                // In-memory cache: hand each tile its own copy so tile disposal doesn't free the cached image.
                if (_previewCache.TryGetValue(doc.id, out var cachedImg) && cachedImg != null)
                {
                    if (!tile.IsDisposed) tile.SetImage(new Bitmap(cachedImg));
                    return;
                }

                Bitmap bmp = null;
                if (sticker)
                {
                    string path = StickerCachePath(doc.id);
                    if (File.Exists(path))
                        try { using (var fs = File.OpenRead(path)) using (var t = Image.FromStream(fs)) bmp = new Bitmap(t); } catch { }

                    if (bmp == null && doc.mime_type == "image/webp")   // static WEBP = the image itself
                    {
                        System.Diagnostics.Debug.WriteLine("[PICKER] preview download webp id=" + doc.id);
                        var full = await _service.DownloadDocBytesAsync(doc);
                        if (full != null) bmp = ImageDecoder.DecodeAny(full);
                        if (bmp != null)
                            try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    }
                    if (bmp == null && doc.mime_type == "application/x-tgsticker" && RLottie.Available)
                    {
                        // Animated .tgs: panel shows a static first frame (rendered once, then cached);
                        // the chat bubble loops the full animation. Keeps the grid light on ARM32.
                        System.Diagnostics.Debug.WriteLine("[PICKER] preview download tgs id=" + doc.id);
                        var tgs = await _service.DownloadDocBytesAsync(doc);
                        using (var clip = RLottie.OpenTgs(tgs))
                            if (clip != null) bmp = clip.RenderFrame(0, 128);
                        if (bmp != null)
                            try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    }
                    if (bmp == null && doc.mime_type == "video/webm")   // WebM sticker: STATIC Telegram thumb — the grid never decodes video
                    {
                        System.Diagnostics.Debug.WriteLine("[PICKER] preview download webm-thumb id=" + doc.id);
                        var thumb = await _service.DownloadThumbAsync(doc);
                        if (thumb != null) bmp = ImageDecoder.DecodeAny(thumb);
                        if (bmp != null)
                            try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    }
                    if (bmp == null)   // still nothing (no native lib / decode failed) → representative emoji
                    {
                        string alt = doc.attributes?.OfType<DocumentAttributeSticker>().FirstOrDefault()?.alt;
                        var em = !string.IsNullOrEmpty(alt) ? EmojiRenderer.Get(alt) : null;
                        if (em != null && !tile.IsDisposed) { tile.SetImage(new Bitmap(em)); return; }
                    }
                }
                else   // GIF poster (shares the chat's thumb_<id>.png disk cache)
                {
                    string tp = MediaCache.ThumbCachePath(doc.id);
                    if (File.Exists(tp))
                        try { using (var fs = File.OpenRead(tp)) using (var t = Image.FromStream(fs)) bmp = new Bitmap(t); } catch { }
                    if (bmp == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[PICKER] preview download gif-thumb id=" + doc.id);
                        var thumb = await _service.DownloadThumbAsync(doc);
                        if (thumb != null) bmp = ImageDecoder.DecodeAny(thumb);
                        if (bmp != null)
                            try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); bmp.Save(tp, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    }
                    if (bmp == null)   // no document thumb → decode the first frame (thumbless GIFs — like official Telegram)
                    {
                        System.Diagnostics.Debug.WriteLine("[PICKER] gif no thumb -> first-frame fallback id=" + doc.id);
                        string clip = MediaCache.ThumbPath("gif_" + doc.id + ".mp4");
                        if (!(File.Exists(clip) && new FileInfo(clip).Length > 0))
                        {
                            string tmp = clip + ".tmp";
                            try
                            {
                                MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
                                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                                await _service.DownloadDocumentToFileAsync(doc, tmp);
                                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0) { if (File.Exists(clip)) File.Delete(clip); File.Move(tmp, clip); }
                            }
                            catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                        }
                        if (File.Exists(clip) && new FileInfo(clip).Length > 0)
                        {
                            var vid = doc.attributes?.OfType<DocumentAttributeVideo>().FirstOrDefault();
                            int fw = vid != null && vid.w > 0 ? vid.w : 320, fh = vid != null && vid.h > 0 ? vid.h : 240;
                            bmp = await System.Threading.Tasks.Task.Run(() => TelegArm.UI.Controls.WebmAnimator.GrabFirstFrame(clip, fw, fh));
                            if (bmp != null)
                                try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); bmp.Save(tp, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                        }
                    }
                }

                if (bmp != null)
                {
                    try { _previewCache[doc.id] = new Bitmap(bmp); } catch { }
                    if (!tile.IsDisposed) tile.SetImage(bmp); else bmp.Dispose();
                }
            }
            catch (Exception ex) { CrashLog.RecordThrottled("async-void:EmojiPicker.LoadPreview", ex); }
        }

        private async System.Threading.Tasks.Task SearchStickers(string emoji)
        {
            ClearFlow(_stickerFlow);
            var docs = await _service.SearchStickersAsync(emoji);
            if (IsDisposed) return;
            foreach (var doc in docs)
                if (doc != null) AddTile(_stickerFlow, doc, 72, true);
        }

        /// <summary>Sets grey placeholder (cue banner) text on a TextBox via EM_SETCUEBANNER.</summary>
        private static void SetCue(TextBox box, string cue)
        {
            try { SendMessage(box.Handle, 0x1501, (IntPtr)1, cue); } catch { }
            box.HandleCreated += (s, e) => { try { SendMessage(box.Handle, 0x1501, (IntPtr)1, cue); } catch { } };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>One sticker/GIF cell: shows a downloaded preview (set later), click to send.
        /// Step 3.1: its side is LOGICAL; <see cref="SetDpi"/> SETS the px size, margin and paint inset.</summary>
        private sealed class MediaTile : Control
        {
            private Image _img;
            private readonly bool _dark;
            private readonly int _logical;   // the side at 96 DPI (72 sticker, 96 GIF)
            private int _dpi;
            private bool _hover;

            public MediaTile(Document doc, int size, bool dark, int dpi)
            {
                _dark = dark;
                _logical = size;
                SetDpi(dpi);
                Cursor = Cursors.Hand;
                TabStop = false;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            public void SetDpi(int dpi)
            {
                _dpi = dpi;
                int s = Dpi.Px(_logical, dpi);
                Size = new Size(s, s);
                Margin = new Padding(Dpi.Px(3, dpi));
                Invalidate();
            }

            public void SetImage(Image img) { _img = img; if (!IsDisposed) Invalidate(); }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(_hover ? (ThemeHelper.PaletteFor(_dark).EmojiHover)
                               : (Parent != null ? Parent.BackColor : BackColor));
                if (_img == null) return;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int inset = Dpi.Px(6, _dpi);
                double scale = Math.Min((double)(Width - inset) / _img.Width, (double)(Height - inset) / _img.Height);
                int w = Math.Max(1, (int)(_img.Width * scale)), h = Math.Max(1, (int)(_img.Height * scale));
                g.DrawImage(_img, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _img != null) { try { _img.Dispose(); } catch { } _img = null; }
                base.Dispose(disposing);
            }
        }

        // ── Emoji grid (virtualized) ─────────────────────────────────────────
        /// <summary>Step 3.1: Cell / Pad / HeaderH are LOGICAL, laid out through Px at the DPI the picker gives it
        /// (<see cref="SetDpi"/>, then a relayout); the emoji image is GetScaled at Px(24) — fetched per paint, never kept.</summary>
        private sealed class Canvas : Control
        {
            private const int Cell = 34, Pad = 6, HeaderH = 26, Glyph = 24;   // logical px
            private readonly List<EmojiRenderer.EmojiCell> _cells;
            private readonly bool _dark;
            private int _dpi;
            private int P(int n) { return Dpi.Px(n, _dpi); }
            private readonly List<KeyValuePair<EmojiRenderer.EmojiCell, Rectangle>> _layout = new List<KeyValuePair<EmojiRenderer.EmojiCell, Rectangle>>();
            private readonly List<string> _categories = new List<string>();
            private readonly Dictionary<string, string> _firstEmoji = new Dictionary<string, string>();
            private readonly Dictionary<string, int> _headerTop = new Dictionary<string, int>();
            private int _hover = -1;

            public event Action<string> Picked;

            public IReadOnlyList<string> Categories => _categories;
            public string FirstEmojiOf(string header) => _firstEmoji.TryGetValue(header, out var e) ? e : null;
            public int TopOf(string header) => _headerTop.TryGetValue(header, out var y) ? y : 0;

            public Canvas(List<EmojiRenderer.EmojiCell> cells, bool dark, int dpi)
            {
                _cells = cells ?? new List<EmojiRenderer.EmojiCell>();
                _dark = dark;
                _dpi = dpi;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Width = P(360);

                string last = null;
                foreach (var c in _cells)
                {
                    if (c.IsHeader) { _categories.Add(c.Header); _firstEmoji[c.Header] = null; last = c.Header; }
                    else if (last != null && _firstEmoji[last] == null) _firstEmoji[last] = c.Emoji;
                }
            }

            /// <summary>The DPI to lay out and paint at; the caller relayouts (SetWidth) after.</summary>
            public void SetDpi(int dpi) { if (dpi > 0) _dpi = dpi; }

            public void SetWidth(int w)
            {
                int min = P(Cell) + 2 * P(Pad);
                if (w < min) w = min;
                Width = w;
                Relayout();
            }

            private void Relayout()
            {
                _layout.Clear();
                int cell = P(Cell), pad = P(Pad), headerH = P(HeaderH);
                int x = pad, y = pad;
                foreach (var c in _cells)
                {
                    if (c.IsHeader)
                    {
                        if (x > pad) { x = pad; y += cell; }
                        _headerTop[c.Header] = y;
                        _layout.Add(new KeyValuePair<EmojiRenderer.EmojiCell, Rectangle>(c, new Rectangle(pad, y, Width - 2 * pad, headerH)));
                        y += headerH; x = pad;
                    }
                    else
                    {
                        if (x + cell > Width - pad) { x = pad; y += cell; }
                        _layout.Add(new KeyValuePair<EmojiRenderer.EmojiCell, Rectangle>(c, new Rectangle(x, y, cell, cell)));
                        x += cell;
                    }
                }
                Height = y + cell + pad;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(BackColor);
                var clip = e.ClipRectangle;
                Color headerColor = ThemeHelper.PaletteFor(_dark).EmojiSectionHeader;
                var headerFont = FontHelper.UiAt(8.5f, _dpi, FontStyle.Bold);   // shared: never dispose
                int one = P(1), radius = P(6), side = P(Glyph);

                for (int i = 0; i < _layout.Count; i++)
                {
                    var rect = _layout[i].Value;
                    if (rect.Bottom < clip.Top || rect.Top > clip.Bottom) continue;
                    var c = _layout[i].Key;
                    if (c.IsHeader)
                    {
                        TextRenderer.DrawText(g, c.Header.ToUpperInvariant(), headerFont, rect, headerColor,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                        continue;
                    }
                    if (i == _hover)
                        using (var hb = new SolidBrush(ThemeHelper.PaletteFor(_dark).EmojiSectionBar))
                        using (var path = DrawHelper.RoundedRect(new Rectangle(rect.X + one, rect.Y + one, rect.Width - 2 * one, rect.Height - 2 * one), radius))
                            g.FillPath(hb, path);

                    // ⚠ BATCH-TA-23/D1a — PRE-SCALED, NOT RESAMPLED PER PAINT.
                    // This used to be EmojiRenderer.Get (the FULL-SIZE 72x72 bitmap) drawn into a 24x24 rect
                    // with InterpolationMode.HighQualityBicubic — i.e. every visible cell was resampled on
                    // EVERY repaint. MEASURED on the x64 dev box, warm cache, real OnPaint over a 360x300
                    // clip (81 visible cells): 8.07 ms per paint, of which 8.00 ms was the resample. The same
                    // 81 draws from pre-scaled 24x24 bitmaps cost 0.45 ms — 17.6x cheaper.
                    // It never mattered while this was a POPUP (one paint, on open). It matters now that the
                    // same grid is docked, because a dock repaints on every scroll step, on a Tegra 3.
                    // GetScaled caches by (emoji, size), so the bicubic work happens ONCE per glyph per size
                    // for the life of the process and the paint becomes a 1:1 blit.
                    // Step 3.1: the side is Px(24) at the canvas DPI, centred in the cell (at 96: 24 at +5, as before).
                    // GetScaledAt records the LOGICAL 24, so step 3.5's trim keeps Px(24) for every open window's DPI; the
                    // image is fetched per paint and never kept, since TrimScaled disposes the sizes it drops.
                    int off = (rect.Width - side) / 2;
                    var img = EmojiRenderer.GetScaledAt(c.Emoji, Glyph, _dpi);
                    if (img != null)
                        g.DrawImage(img, rect.X + off, rect.Y + off, side, side);   // 1:1 — no interpolation
                    else
                    {
                        // Shared: never dispose. (Pre-existing fallback when the Noto zip is missing — font text.)
                        TextRenderer.DrawText(g, c.Emoji, FontCache.Get("Segoe UI Emoji", 13f, FontStyle.Regular, _dpi), rect,
                            _dark ? Color.White : Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
            }

            private int HitTest(Point p)
            {
                for (int i = 0; i < _layout.Count; i++)
                    if (!_layout[i].Key.IsHeader && _layout[i].Value.Contains(p)) return i;
                return -1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int h = HitTest(e.Location);
                if (h != _hover) { _hover = h; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Invalidate(); } }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                int h = HitTest(e.Location);
                if (h >= 0) Picked?.Invoke(_layout[h].Key.Emoji);
            }
        }
    }
}
