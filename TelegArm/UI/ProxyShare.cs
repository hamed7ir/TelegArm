using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// BATCH-TA-18a — Share (copy link) and Get QR Code, in ONE place.
    ///
    /// Two surfaces offer these: the ⋮ menu on <see cref="ProxyLinkForm"/> (a link just tapped in a chat)
    /// and the row menu in <see cref="ProxyForm"/> (a proxy already saved). They must behave identically —
    /// same clipboard payload, same QR, same secret-handling rules — so they share an implementation rather
    /// than each growing their own. A second copy is how one of them ends up logging the secret.
    ///
    /// ⚠⚠ THE SECRET RULE, RESTATED BECAUSE THIS FILE IS WHERE IT IS EASIEST TO BREAK. ⚠⚠
    /// The clipboard and the QR both carry the FULL link, secret included — that is the point of sharing.
    /// What must never happen is the secret reaching durable storage we control: no log line beyond
    /// <see cref="ProxyUrl.SafeForLog"/> (host:port), no temp file, no saved image, no Clipboard.SetImage.
    /// §2d's rule covers anything DERIVED from the secret, and a QR is the secret in another alphabet.
    ///
    /// Redesign step 2.6b: this is a static helper, not a form — the one window it opens, the QR sheet, is a private
    /// <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). The public surface is unchanged.
    /// </summary>
    public static class ProxyShare
    {
        /// <summary>Adds "Share (copy link)" and "Get QR Code" to a menu, both acting on
        /// <paramref name="url"/> — the proxy the CALLER is showing, never the active one.</summary>
        /// <param name="feedback">Optional surface-specific confirmation (text, ok). Null = silent.</param>
        public static void AddMenuItems(ContextMenuStrip menu, IWin32Window owner, string url,
                                        Action<string, bool> feedback = null)
        {
            if (menu == null || string.IsNullOrEmpty(url)) return;

            var share = new ToolStripMenuItem("Share (copy link)");
            share.Click += (s, e) =>
            {
                bool ok = CopyLink(url);
                if (feedback != null) feedback(ok ? "Link copied" : "Couldn't copy", ok);
            };
            menu.Items.Add(share);

            var qr = new ToolStripMenuItem("Get QR Code");
            // Deferred: let the menu close before a modal opens over it.
            var ownerControl = owner as Control;
            qr.Click += (s, e) =>
            {
                if (ownerControl != null && !ownerControl.IsDisposed)
                    ownerControl.BeginInvoke((Action)(() => ShowQr(owner, url)));
                else ShowQr(owner, url);
            };
            menu.Items.Add(qr);
        }

        /// <summary>Copies the NORMALISED link. What lands on the clipboard is exactly what
        /// <see cref="ProxyUrl.TryNormalize"/> emitted, and that form is idempotent — so pasting it back
        /// into ProxyForm re-validates through the SAME validator that accepted it in the first place
        /// (measured: mdfiles/probes/ShareProbe.cs). Returns false if the clipboard refused.</summary>
        public static bool CopyLink(string url)
        {
            bool ok = true;
            try { Clipboard.SetText(url ?? ""); } catch { ok = false; }
            // The clipboard holds the secret because the user asked for it. The LOG still may not.
            Logger.Diag("[PROXYSHARE] link copied for " + ProxyUrl.SafeForLog(url) + (ok ? "" : " — clipboard FAILED"));
            return ok;
        }

        /// <summary>The full link as a QR, RENDERED IN MEMORY ONLY.
        /// <see cref="QrControl"/> computes a module matrix and draws it with GDI+ inside OnPaint; it never
        /// produces a file, and nothing added here may either — see this class's remarks.</summary>
        public static void ShowQr(IWin32Window owner, string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            Logger.Diag("[PROXYSHARE] QR shown for " + ProxyUrl.SafeForLog(url));   // host:port — NEVER the payload

            using (var f = new QrDialog(url))
            {
                if (owner != null) f.ShowDialog(owner); else f.ShowDialog();
            }
        }

        /// <summary>The QR sheet: the code and, under it, host:port. Esc, Enter, Close and ✕ all dismiss it.</summary>
        private sealed class QrDialog : DialogForm
        {
            private const int SheetW = 360;

            public QrDialog(string url) : base("Proxy QR Code", SheetW, 480)
            {
                PopulateBody(new QrRow(url));
                var close = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
                CancelButton = close;
                AcceptButton = close;
                FitToContent();
            }
        }

        /// <summary>The code, centred in the row, with host:port under it (the caption — never the payload).
        /// A SettingRow so it brings the kit's DPI refit and theme handling; it paints on the window colour.
        /// ⚠ BLACK ON WHITE REGARDLESS OF THEME. A theme-tinted QR is a QR that doesn't scan, and that failure
        /// looks like a bad link rather than a bad render.</summary>
        private sealed class QrRow : SettingRow
        {
            private const int Side = 300, PadTop = 12, PadBottom = 10, CaptionGap = 4, CaptionH = 20;
            private const float CapPt = 9f;
            private readonly QrControl _qr;

            public QrRow(string url) : base(ProxyUrl.SafeForLog(url), false)
            {
                _qr = new QrControl { Dark = Color.Black, Light = Color.White, AccessibleName = "QR code" };
                _qr.SetPayload(url);
                Controls.Add(_qr);
                AccessibleRole = AccessibleRole.Grouping;
                Refit();
            }

            public override int HeightFor(int width, int dpi)
            {
                return Dpi.Px(PadTop + Side + CaptionGap + CaptionH + PadBottom, dpi);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (_qr == null) return;
                int dpi = Dpi.Of(this);
                int side = Math.Max(1, Math.Min(Dpi.Px(Side, dpi), Width - 2 * Dpi.Px(PadX, dpi)));
                _qr.SetBounds((Width - side) / 2, Dpi.Px(PadTop, dpi), side, side);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
                PaintBack(g, p);
                if (Text.Length == 0 || _qr == null) return;
                var r = new Rectangle(padX, _qr.Bottom + Dpi.Px(CaptionGap, dpi), Math.Max(1, Width - 2 * padX), Dpi.Px(CaptionH, dpi));
                TextRenderer.DrawText(g, Text, FontHelper.ForAt(Text, CapPt, dpi), r, p.TextDim,
                    BaseFlags | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
