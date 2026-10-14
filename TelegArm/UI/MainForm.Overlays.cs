using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── "Connecting… / Waiting for network" overlay (startup never exits on a missing VPN) ──

        // ── Account-switch transition: one calm overlay (avatar + "Switching to [Name]") covering the whole
        //    switch (teardown → connect → reload), so the user sees a steady transition instead of the UI
        //    blanking + a connect popup + a load flash. The Retry/Cancel connect card layers over it if a
        //    (still-cold) connect is slow — graceful. (Warm-core swap = a separate staged batch.)
        private void EnsureSwitchOverlay()
        {
            if (_switchOverlay != null) return;
            _switchOverlay = new Panel
            {
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = _dark ? Color.FromArgb(26, 26, 29) : Color.FromArgb(244, 244, 247)
            };
            _switchOverlay.Paint += PaintSwitchOverlay;
            Content.Controls.Add(_switchOverlay);   // 2.7a: under the title bar
            _switchDots = new System.Windows.Forms.Timer { Interval = 350 };
            _switchDots.Tick += (s, e) => { _switchDotCount = (_switchDotCount + 1) % 4; if (_switchOverlay.Visible) _switchOverlay.Invalidate(); };
        }

        private void PaintSwitchOverlay(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int cx = _switchOverlay.Width / 2, cy = _switchOverlay.Height / 2;
            int d = 100; var rect = new Rectangle(cx - d / 2, cy - d / 2 - 44, d, d);
            if (_switchAvatar != null) DrawHelper.DrawCircularImage(g, rect, _switchAvatar);
            else
            {
                using (var b = new SolidBrush(_accent)) g.FillEllipse(b, rect);
                string ini = !string.IsNullOrEmpty(_switchOverlayName) ? _switchOverlayName.Substring(0, 1).ToUpperInvariant() : "?";
                using (var f = new Font("Segoe UI", 36f, FontStyle.Bold))
                    TextRenderer.DrawText(g, ini, f, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            string title = "Switching to " + (_switchOverlayName ?? "account") + new string('.', _switchDotCount);
            using (var f = new Font("Segoe UI", 13.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, title, f, new Rectangle(0, cy + 30, _switchOverlay.Width, 34),
                    _dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void ShowSwitchOverlay(long targetId, string name)
        {
            EnsureSwitchOverlay();
            _switchOverlayName = name;
            if (_switchAvatar != null) { try { _switchAvatar.Dispose(); } catch { } _switchAvatar = null; }
            try
            {
                var info = AccountStore.ListAccounts().FirstOrDefault(a => a.Id == targetId);
                if (info != null && !string.IsNullOrEmpty(info.AvatarPath) && File.Exists(info.AvatarPath))
                    using (var fs = File.OpenRead(info.AvatarPath)) using (var t = Image.FromStream(fs)) _switchAvatar = new Bitmap(t);
            }
            catch { _switchAvatar = null; }
            _switchDotCount = 0;
            _switchOverlay.Bounds = Content.ClientRectangle;   // cover the full client area (not docked — avoids layout fights)
            _switchOverlay.Visible = true;
            _switchOverlay.BringToFront();
            if (_switchDots != null && !_switchDots.Enabled) _switchDots.Start();
            System.Diagnostics.Debug.WriteLine("[ACCT] switch overlay shown → " + name);
        }

        private void HideSwitchOverlay()
        {
            if (_switchOverlay == null) return;
            if (_switchDots != null) _switchDots.Stop();
            _switchOverlay.Visible = false;
            if (_switchAvatar != null) { try { _switchAvatar.Dispose(); } catch { } _switchAvatar = null; }
        }

        /// <summary>BATCH-TA-16f/F1 — lays out the connecting overlay's action row. There are THREE possible
        /// buttons and room for two, so this is the one place that decides which pair is showing.
        ///
        /// DURING A SWITCH: Retry + Cancel. Cancel wins the second slot because the user is NOT stranded —
        /// aborting restores the account that was already working, which is the more urgent escape hatch.
        /// OTHERWISE (the ordinary blocked-network resume): Retry + Proxy. THIS is the stranded case F1 is
        /// about — no chat list, no pill (D3 hides it under this overlay), and previously no way to reach
        /// proxy settings at all.
        /// Called from both construction and ShowConnecting, so the two can never disagree.</summary>
        private void LayoutConnectingButtons()
        {
            if (_connectingPanel == null || _retryButton == null) return;
            bool switching = _switchInProgress;
            if (_switchCancelButton != null) _switchCancelButton.Visible = switching;
            if (_proxyOverlayButton != null) _proxyOverlayButton.Visible = !switching;

            _retryButton.Location = new Point(_connectingPanel.Width / 2 - _retryButton.Width - 6, 118);
            var second = switching ? _switchCancelButton : _proxyOverlayButton;
            if (second != null) second.Location = new Point(_connectingPanel.Width / 2 + 6, 118);
        }

        private void EnsureConnectingUi()
        {
            if (_connectingPanel != null) return;
            // UI-FIX-T1: no local `dark` snapshot — the Paint lambda reads _dark LIVE (captured colors were the
            // stale-after-theme-switch bug, UI-AUDIT A.3); RecolorConnectingUi re-pushes the property colors.
            _connectingPanel = new Panel
            {
                Size = new Size(460, 172),
                BackColor = _dark ? Color.FromArgb(44, 44, 48) : Color.FromArgb(250, 250, 252),
                Visible = false
            };
            _connectingPanel.Paint += (s, e) =>
            {
                using (var pen = new Pen(_dark ? Color.FromArgb(72, 72, 78) : Color.FromArgb(214, 214, 220)))
                    e.Graphics.DrawRectangle(pen, 0, 0, _connectingPanel.Width - 1, _connectingPanel.Height - 1);
            };

            _connectingTitle = new Label
            {
                Text = "Connecting…",
                AutoSize = false, Size = new Size(428, 34), Location = new Point(16, 20),
                TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = _dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38)
            };
            _connectingDetail = new Label
            {
                Text = "Waiting for network — make sure your VPN is on.",
                AutoSize = false, Size = new Size(428, 46), Location = new Point(16, 58),
                TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.75f),
                ForeColor = _dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(108, 108, 114)
            };
            _retryButton = new RoundedButton   // 2.7a: was a MaterialButton (Contained)
            {
                Text = "Retry now", Kind = RoundedButtonKind.Primary, Modern = true,
                Size = new Size(170, 40), Font = FontHelper.UiAt(10f, WindowDpiValue, FontStyle.Bold)
            };
            // BATCH-TA-16f/F1 — the two buttons share the row, so Retry no longer centres alone.
            _retryButton.Location = new Point(_connectingPanel.Width / 2 - _retryButton.Width - 6, 118);
            _retryButton.Click += (s, e) =>
            {
                var c = _retryNowCts;   // cut the backoff wait → retry immediately
                if (c != null) { try { c.Cancel(); } catch { } }
            };

            // ⚠ BATCH-TA-16f/F1 — THE SECOND STRANDED SURFACE. This overlay is what a user stares at when
            // the network is blocking Telegram ("Waiting for network — make sure your VPN is on"), and it
            // covers the chat list, so the floating proxy pill is deliberately hidden underneath it
            // (TA-16d/D3: two connection indicators disagreeing reads as a bug). The consequence, missed
            // until now, is that this screen offered ONLY "Retry now" and "Cancel" — retrying forever on a
            // network that will never work, with the one setting that could fix it unreachable. A proxy is
            // needed EXACTLY here. Opens the same ProxyForm as the login pill and the Settings row.
            _proxyOverlayButton = new RoundedButton   // 2.7a: was a MaterialButton (Outlined)
            {
                Text = "Proxy", Kind = RoundedButtonKind.Secondary, Modern = true,
                Size = new Size(120, 40), Font = FontHelper.UiAt(10f, WindowDpiValue, FontStyle.Bold)
            };
            _proxyOverlayButton.Location = new Point(_connectingPanel.Width / 2 + 6, 118);
            _proxyOverlayButton.Click += (s, e) => OpenProxySettings();

            _switchCancelButton = new RoundedButton   // 2.7a: was a MaterialButton (Outlined)
            {
                Text = "Cancel", Kind = RoundedButtonKind.Neutral, Modern = true,
                Size = new Size(150, 40), Visible = false, Font = FontHelper.UiAt(10f, WindowDpiValue, FontStyle.Bold)
            };
            _switchCancelButton.Click += (s, e) =>
            {
                if (!_switchInProgress) return;
                _switchAborted = true;
                try { if (_abortConnect != null) _abortConnect.Cancel(); } catch { }
                var c = _retryNowCts; if (c != null) { try { c.Cancel(); } catch { } }   // wake the current wait
            };

            _connectingPanel.Controls.Add(_connectingTitle);
            _connectingPanel.Controls.Add(_connectingDetail);
            _connectingPanel.Controls.Add(_retryButton);
            _connectingPanel.Controls.Add(_proxyOverlayButton);
            _connectingPanel.Controls.Add(_switchCancelButton);
            Content.Controls.Add(_connectingPanel);
            _connectingPanel.BringToFront();
            LayoutConnectingButtons();   // F1: one owner of the action-row layout

            _connectingDots = new System.Windows.Forms.Timer { Interval = 450 };
            _connectingDots.Tick += (s, e) =>
            {
                _dotPhase = (_dotPhase + 1) % 4;
                if (_connectingTitle != null) _connectingTitle.Text = "Connecting" + new string('.', _dotPhase);
            };

            Resize += (s, e) => CenterConnectingPanel();
            CenterConnectingPanel();
        }

        /// <summary>UI-FIX-T1: re-pushes the connect card's construction-time colors on a live theme switch
        /// (the border pen already derives from _dark at paint time). No-op until the card exists.</summary>
        private void RecolorConnectingUi()
        {
            if (_connectingPanel == null) return;
            _connectingPanel.BackColor = _dark ? Color.FromArgb(44, 44, 48) : Color.FromArgb(250, 250, 252);
            if (_connectingTitle != null) _connectingTitle.ForeColor = _dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38);
            if (_connectingDetail != null) _connectingDetail.ForeColor = _dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(108, 108, 114);
            _connectingPanel.Invalidate(true);
        }

        private void CenterConnectingPanel()
        {
            if (_connectingPanel == null) return;
            _connectingPanel.Location = new Point(
                Math.Max(0, (Content.ClientSize.Width - _connectingPanel.Width) / 2),
                Math.Max(0, (Content.ClientSize.Height - _connectingPanel.Height) / 2));
        }

        private bool _connectingShown;   // whether the overlay is up — Visible would read false while the window is in the tray

        private void ShowConnecting(string detail)
        {
            EnsureConnectingUi();
            _chatTitle.Text = "Connecting…";
            if (_connectingDetail != null) _connectingDetail.Text = detail;
            LayoutConnectingButtons();
            if (!_connectingPanel.Visible) _connectingPanel.Visible = true;
            _connectingPanel.BringToFront();
            _connectingShown = true;
            RefreshProxyPill();   // D3: the overlay owns connection state while it is up → pill hides
            CenterConnectingPanel();
            if (_connectingDots != null && !_connectingDots.Enabled) _connectingDots.Start();
        }

        private void HideConnecting()
        {
            if (_connectingDots != null) _connectingDots.Stop();
            if (_connectingPanel != null) _connectingPanel.Visible = false;
            _connectingShown = false;
            RefreshProxyPill();   // D3: overlay gone → the pill may report the proxy dimension again
            if (_retryNowCts != null) { try { _retryNowCts.Cancel(); } catch { } }
        }

        private void SetConnectingDetail(string detail)
        {
            if (_connectingDetail != null) _connectingDetail.Text = detail;
        }

    }
}
