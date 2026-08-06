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

            bool dark = ThemeHelper.IsDark;
            Color accent = ThemeHelper.GetWindowsAccentColor();
            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            Color sub = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(120, 120, 126);

            const int Side = 300, Pad = 16;
            using (var f = new Form())
            {
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.Font = FontHelper.Ui(9.5f);
                // ⚠ + BarH — ThemedChrome carves its caption OUT of the client area (ThemedChrome.cs:11).
                f.ClientSize = new Size(Side + Pad * 2, Side + Pad * 2 + 22 + ThemedChrome.BarH);
                var content = ThemedChrome.Apply(f, "Proxy QR Code", accent, dark);
                content.BackColor = bg;

                // ⚠ BLACK ON WHITE REGARDLESS OF THEME. A theme-tinted QR is a QR that doesn't scan, and
                // that failure looks like a bad link rather than a bad render.
                var qr = new QrControl
                {
                    Left = Pad, Top = Pad, Width = Side, Height = Side,
                    Dark = Color.Black, Light = Color.White
                };
                qr.SetPayload(url);
                content.Controls.Add(qr);

                var cap = new Label
                {
                    Left = Pad, Top = Pad + Side + 2, Width = Side, Height = 20,
                    Text = ProxyUrl.SafeForLog(url), ForeColor = sub, BackColor = bg,
                    Font = FontHelper.Ui(9f), TextAlign = ContentAlignment.MiddleCenter
                };
                content.Controls.Add(cap);

                if (owner != null) f.ShowDialog(owner); else f.ShowDialog();
            }
        }
    }
}
