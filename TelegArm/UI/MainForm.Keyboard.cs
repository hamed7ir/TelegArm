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
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ── On-screen keyboard (TabTip) detection → shrink the window above it (Part B) ──
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect r);
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        // KBD-DISMISS-BLUR 0.2(d): DWM cloak state — the touch-keyboard window can be DWM-CLOAKED (effectively
        // hidden) while IsWindowVisible still reads TRUE and the rect stays on-screen. Nonzero == cloaked ==
        // dismissed. Guarded at the call site: if dwmapi is unavailable on RT we fall back to the rect checks.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        private const int DWMWA_CLOAKED = 14;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
        // COMPOSER-input-diagnose: the thread's real Win32 keyboard focus — reveals whether WM_CHAR is being
        // delivered to the inner EDIT (ThemedTextBox.Inner → inserts) or the outer ThemedTextBox host (no EDIT
        // → char dropped).
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();
        // COMPOSER-native-input-diagnose v2.1 — message-level facts (mods/sent) + the H1 normalized-resend test,
        // + runtime-state probes (window style / IME context) to close H3'.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool InSendMessage();
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);   // 32-bit process (Prefer32Bit) → GetWindowLong is correct
        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        // NOTE (dead ends, tested on RT — do not re-add): the IMM sever (ImmAssociateContext NULL) was INERT
        // (CUAS attaches per-THREAD, not per-window-IMC); SetInputScope(IS_PASSWORD) did NOT fix the dead mode
        // and made the CUAS grab DETERMINISTIC (13+ consecutive all-dead connections vs. dead↔native flipping
        // before it) — both removed. The productized shim (EditInputProbe) is the mechanism that carries input.
        private const int GWL_STYLE = -16, ES_READONLY = 0x0800;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        // v3 — the activation hypothesis + input-source self-labelling.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct INPUT_MESSAGE_SOURCE { public int deviceType; public int originId; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source);

        private Timer _kbTimer;
        private bool _kbActive;
        private bool _kbShrinkActive;     // KBD-RESTORE: spans shrink → restore-complete; capture forbidden while set
        private bool _kbLastShowing;      // for logging the show/hide transition
        private bool _kbHwSuppressed;     // one-shot log guard: OSK detected but a hardware keyboard is attached
        private string _lastOcc = "";     // last raw InputPane OccludedRect (for the [KBD] log)
        // KBD-DISMISS-BLUR: on OSK dismissal, blur the composer so Windows has no focused field to re-summon for.
        private Control _kbFocusSink;         // neutral SELECTABLE non-text sink; BlurForDismiss parks focus here (v3)
        private const int KbArmedTickMs = 120;         // poll cadence while the keyboard is up (armed) — catch the re-summon fast
        private const int KbIdleTickMs = 350;          // poll cadence when idle (the original interval)
        // KBD-DISMISS-v3.1: post-dismiss STATE suppression, gated on input PROVENANCE (RT log proved a real tap is a
        // WM_POINTERDOWN originId=hardware, while every spurious OS/TabTip re-focus is a dev0/org0 injected click).
        // While suppressed, bounce EVERY composer/search focus back to the sink UNTIL a genuine hardware tap on the
        // box — no time bound, so the 3-5s async re-focus can't slip through as the cooldown let it.
        private bool _kbSuppressed;                     // true from a dismiss until a genuine hardware tap re-engages the box
        private int _lastRealEditTouchTick;            // TickCount of the last HARDWARE-origin pointer-down on a composer/search EDIT
        private const int RealTouchWindowMs = 1500;    // a genuine tap's pointer-down lands within this of the focus it triggers
        // KBD-CLOSE-PROBE (instrument only): capture the foreground-✕ moment. _ttLast dedups the per-tick TabTip state
        // dump (log on CHANGE, not 120ms spam); _lastComposerKeyTick separates "actively typing" from "idle keyboard up".
        private string _ttLast = "";
        private int _lastComposerKeyTick;
        private bool _ttCloakedPrev;   // KBD-CLOAK-BLUR: last tick's TabTip cloak state, to detect the False→True dismiss edge
        // ── Part B: shrink the window above the on-screen keyboard (TabTip), restore on hide ──

        /// <summary>Starts the guarded poller that shrinks the window above the touch keyboard. Best-effort:
        /// any failure leaves the app full-size (this enhancement must NEVER break the app).</summary>
        private void StartKeyboardWatch()
        {
            try
            {
                Helpers.TouchKeyboard.HasHardwareKeyboard();   // log the initial [KBD] hardware keyboard= verdict at startup
                Logger.Diag("[INPUT] " + InputPolicy.Summary);   // device trip: every input switch, once at startup
                EnsureFocusSink();   // KBD-DISMISS-v3 PART 1: the neutral parking control must exist BEFORE any dismiss
                _kbTimer = new Timer { Interval = 350 };
                _kbTimer.Tick += OnKbTick;
                _kbTimer.Start();
            }
            catch { }
        }

        private void OnKbTick(object sender, EventArgs e)
        {
            try
            {
                if (IsDisposed || WindowState == FormWindowState.Minimized) return;
                if (KeyboardInset.Listening) PublishKeyboardForDialogs();   // redesign 1.4; inert with no DialogForm open

                // The keyboard matters only when a text field is focused (the reliable "keyboard is up" signal —
                // and it means focus-out is a reliable RESTORE trigger, unlike the flaky keyboard-hide detection).
                bool textFocused = (_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus);
                int kbTop = 0; string via = "none";
                bool showing = textFocused && DetectKeyboard(out kbTop, out via) && kbTop > 0;
                // A physical keyboard (Type Cover / USB) is attached → the user isn't using the on-screen
                // keyboard. Windows still auto-pops TabTip on focus on the original Surface RT (it reports slate
                // mode permanently), but we must NOT shrink the window for it. Suppress (and let the restore
                // branch below un-shrink if we were already shrunk).
                if (showing && Helpers.TouchKeyboard.HasHardwareKeyboard())
                {
                    if (!_kbHwSuppressed)
                    {
                        if (LogOn) Logger.Diag("[KBD] OSK detected but a HARDWARE keyboard is attached → NOT shrinking (via=" + via + " kbTop=" + kbTop + ")");
                        _kbHwSuppressed = true;
                    }
                    showing = false;
                }
                else _kbHwSuppressed = false;
                if (KbdDiag && (_kbActive || textFocused)) LogKbSignals(showing ? "shown" : "gone");   // 0.2 raw-signal dump while armed
                if (LogOn && (_kbActive || textFocused)) LogTabTipTick();   // KBD-CLOSE-PROBE: TabTip state (change-detected) around ✕

                // KBD-CLOAK-BLUR: the EARLIEST dismiss signal on RT is TabTip becoming DWM-cloaked (the ✕/close), and it
                // fires while the composer STILL holds focus. Blur at THIS edge — ahead of the HIDE/restore path below —
                // so the field is unfocused before the OS can re-summon (what an Explorer-defocus does, but earlier and
                // without leaving the app). The HIDE branch still runs for the resize; its own blur is then a no-op. Keyed
                // on the RAW cloak edge + live focus (not the `showing` composite), so it fires even if `showing` lags.
                bool ttCloaked = _kbActive && IsTabTipCloaked();
                if (InputPolicy.DismissGuard && ttCloaked && !_ttCloakedPrev
                    && ((_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus)))
                {
                    if (LogOn) Logger.Diag("[KBD] cloak dismiss → pre-emptive blur (composer still focused)");
                    _kbSuppressed = true; _lastRealEditTouchTick = 0;   // arm suppression exactly as the HIDE dismiss branch does
                    BlurForDismiss();                                    // park on the sink NOW, pre-empting the re-summon
                }
                _ttCloakedPrev = ttCloaked;
                if (showing != _kbLastShowing)
                {
                    _kbLastShowing = showing;
                    if (LogOn) Logger.Diag("[KBD] keyboard " + (showing ? "SHOW" : "HIDE")
                        + (showing ? " via=" + via + " kbTop=" + kbTop + " winTop=" + Top + " winH=" + Height + (_lastOcc.Length > 0 ? " occluded=[" + _lastOcc + "]" : "") : ""));
                    if (showing) LogShowCheck();   // Part 1.5: false-SHOW / flicker verification
                }

                if (!InputPolicy.ResizeForKeyboard)
                {
                    if (_kbActive) RestoreFromKeyboard();   // toggle turned off while shrunk → restore
                    return;
                }

                if (showing && !_kbActive)
                {
                    _kbActive = true;
                    // KBD-RESTORE 1.1 CAPTURE GUARD: _savedBounds may be written ONLY when no shrink/restore is
                    // in flight — a SHOW re-fire during the settle window used to capture the shrunk rect (the
                    // RT "1366x384 state=Maximized") or the OS-re-asserted one ("1366x728" → the 600→728 drift).
                    if (!_kbShrinkActive)
                    {
                        bool wasMax = WindowState == FormWindowState.Maximized;
                        _savedState = WindowState;
                        // Maximized: Windows owns that geometry — save the TRUE normal-state rect
                        // (RestoreBounds), never the current Bounds.
                        _savedBounds = wasMax ? RestoreBounds : Bounds;
                        _savedMinH = MinimumSize.Height;
                        if (LogOn) Logger.Diag("[KBD] saved bounds=" + _savedBounds.X + "," + _savedBounds.Y
                            + "," + _savedBounds.Width + "x" + _savedBounds.Height + " wasMax=" + wasMax);
                    }
                    else if (LogOn) Logger.Diag("[KBD] show while shrink active → keeping saved bounds");
                    _kbShrinkActive = true;
                    var wa = Screen.FromControl(this).WorkingArea;
                    int left = Left, width = Width;
                    // 1.2: never leave a Maximized-but-shrunk chimera for the OS to correct — Normal FIRST.
                    if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; left = wa.Left; width = wa.Width; }
                    // FIX 2: the keyboard covers below kbTop; fill from the screen top to just above it. The ctor's
                    // MinimumSize.Height (480) would clamp the shrink (kbTop can leave <480 above), so drop it.
                    // Step 3.4: kbTop / wa are raw screen pixels; the 160 floor, 4 gap and 120 clamp are logical.
                    int h = Math.Max(Px(160), kbTop - wa.Top - Px(4));
                    MinimumSize = new Size(MinimumSize.Width, Px(120));
                    SetBounds(left, wa.Top, width, h);
                    if (LogOn) Logger.Diag("[KBD] resize→ top=" + wa.Top + " h=" + h + " bottom=" + (wa.Top + h) + " (above kbTop=" + kbTop + ")");
                }
                else if (!showing && _kbActive)
                {
                    // KBD-DISMISS-v3.1: the keyboard went away while shrunk. Park focus on the neutral non-text sink FIRST
                    // (so the restore can't re-pick the composer as the active control), THEN restore. Enter SUPPRESSION:
                    // until a genuine hardware tap on the box, every composer/search refocus (the OS/TabTip re-summon,
                    // dev0/org0) is bounced back to the sink — state-based, so the unbounded 3-5s async re-focus can't slip.
                    // Device trip: with the dismiss guard switched off, only the resize is undone (InputPolicy).
                    if (InputPolicy.DismissGuard)
                    {
                        _kbSuppressed = true; _lastRealEditTouchTick = 0;   // stale tap can't authorize a reshow
                        if ((_messageInput != null && _messageInput.ContainsFocus)
                            || (_searchBox != null && _searchBox.ContainsFocus))
                            BlurForDismiss();          // park focus on the sink — the OSK has no text field to re-summon for
                    }
                    RestoreFromKeyboard();         // reclaim the space, AFTER the park
                }

                // Adaptive cadence (1.1): poll fast ONLY while the keyboard is up (armed), slow when idle — not a new
                // always-on high-freq timer (freeze-era discipline holds). Set only on change to avoid restart churn.
                int wantInterval = _kbActive ? KbArmedTickMs : KbIdleTickMs;
                if (_kbTimer != null && _kbTimer.Interval != wantInterval) _kbTimer.Interval = wantInterval;
            }
            catch { /* detection/resize is best-effort — never break the app over the keyboard */ }
        }

        /// <summary>Redesign step 1.4 — Part B stays the ONE keyboard detector; a dialog does not poll. When another of our
        /// windows is active with a text field focused, the same detection runs for it and the keyboard rectangle goes
        /// to <see cref="KeyboardInset"/> (DialogForm shifts itself above it). Same rules as this form's own shrink: no
        /// shift with a hardware keyboard attached or with "Resize for keyboard" off. Runs only while something
        /// listens, so the live app — which opens no DialogForm yet — is unchanged.</summary>
        private void PublishKeyboardForDialogs()
        {
            var kb = Rectangle.Empty;
            try
            {
                var active = Form.ActiveForm;
                if (active != null && !ReferenceEquals(active, this) && active.ContainsFocus && FocusedIsText(active)
                    && InputPolicy.ResizeForKeyboard && !Helpers.TouchKeyboard.HasHardwareKeyboard()
                    && DetectKeyboard(out int kbTop, out string via) && kbTop > 0)
                {
                    var screen = Screen.FromControl(active).Bounds;
                    if (kbTop < screen.Bottom) kb = Rectangle.FromLTRB(screen.Left, kbTop, screen.Right, screen.Bottom);
                }
            }
            catch { kb = Rectangle.Empty; }
            KeyboardInset.Publish(kb);
        }

        private static bool FocusedIsText(Form f)
        {
            Control c = f.ActiveControl;
            while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
            return (c is TextBoxBase || c is UI.Controls.ITextInputTarget) && c.Focused;
        }

        private void RestoreFromKeyboard()
        {
            _kbActive = false;
            try
            {
                // Restore the clamp. Step 3.4: from the LOGICAL minimum at the current DPI, not a pixel value captured
                // at shrink time (stale if the DPI changed while shrunk); the captured height is only the fallback.
                var lm = LogicalMinimumSize;
                if (!lm.IsEmpty) MinimumSize = new Size(Px(lm.Width), Px(lm.Height));
                else if (_savedMinH > 0) MinimumSize = new Size(MinimumSize.Width, _savedMinH);
                bool wasMax = _savedState == FormWindowState.Maximized;
                // 1.2: wasMax → hand the geometry back to Windows (NO SetBounds — the OS owns maximized).
                if (wasMax) WindowState = FormWindowState.Maximized;
                else { WindowState = FormWindowState.Normal; Bounds = _savedBounds; }
                if (LogOn) Logger.Diag("[KBD] restore → " + _savedBounds.Width + "x" + _savedBounds.Height + " wasMax=" + wasMax);
            }
            catch { }
            finally { _kbShrinkActive = false; }   // cleared LAST — closes the capture guard window (1.1)
        }

        /// <summary>True when the docked TabTip keyboard occupies the bottom of the screen; outputs its top edge
        /// (the line the app's bottom should sit just above). Conservative — unknown/undocked states → false.</summary>
        private bool KeyboardShowing(out int kbTop)
        {
            kbTop = 0;
            try
            {
                IntPtr h = FindWindow("IPTip_Main_Window", null);   // the Win8.1/RT touch-keyboard window class
                if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
                if (IsCloaked(h)) return false;   // 0.2(d): DWM-cloaked == effectively hidden (a dismiss signal on 8.1)
                if (!GetWindowRect(h, out NativeRect r)) return false;
                var screen = Screen.FromControl(this).Bounds;
                int height = r.Bottom - r.Top;
                // Step 3.4: tolerances on an OS window sized by the MONITOR's DPI → raw monitor DPI, never the user scale.
                int minH = Dpi.Px(150, MonitorDpiValue), edge = Dpi.Px(80, MonitorDpiValue);
                if (height < minH) return false;                     // too small to be the OSK
                if (r.Top >= screen.Bottom - edge) return false;     // dismissed / moved off the bottom
                if (r.Bottom < screen.Bottom - edge) return false;   // not bottom-docked → not the case we handle
                kbTop = r.Top;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Detects the touch keyboard and its top edge (screen Y). Prefers the WinRT InputPane's
        /// OccludedRect (the ACTUAL covered area — correct for the immersive keyboard on RT); falls back to the
        /// legacy TabTip window rect if InputPane is unavailable. Sets <see cref="_lastOcc"/> for the log.</summary>
        private bool DetectKeyboard(out int kbTop, out string via)
        {
            kbTop = 0; via = "none";
            try
            {
                if (WinRtKeyboard.TryInit(Handle) && WinRtKeyboard.TryOccludedRect(out double ox, out double oy, out double ow, out double oh))
                {
                    via = "InputPane";
                    _lastOcc = ox.ToString("0") + "," + oy.ToString("0") + "," + ow.ToString("0") + "," + oh.ToString("0");
                    // OccludedRect is window-relative (custom-chrome AppForm → client = window): its top (oy)
                    // is where the keyboard starts within the window → screen kbTop = winTop + oy. RAW logged so
                    // the coordinate space can be confirmed from the RT run.
                    // Step 3.4: OccludedRect is in DIPs → physical with the RAW monitor DPI (never the user scale);
                    // _lastOcc above keeps the raw DIP values for the log. Identity at 96.
                    kbTop = Top + (int)Math.Round(oy * MonitorDpiValue / 96.0);
                    return true;
                }
            }
            catch (Exception ex) { Logger.Diag("[KBD] InputPane read failed: " + ex.GetType().Name + ": " + ex.Message); }

            _lastOcc = "";
            if (KeyboardShowing(out kbTop)) { via = "TabTip"; return true; }
            return false;
        }

        /// <summary>0.2(d): true if the window is DWM-cloaked (hidden by the shell while still "visible"). Guarded —
        /// if dwmapi/the attribute is unavailable (RT), returns false so detection falls back to the rect checks.</summary>
        private static bool IsCloaked(IntPtr hwnd)
        {
            try { return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0; }
            catch { return false; }
        }

        /// <summary>KBD-CLOAK-BLUR: is the TabTip touch-keyboard window currently DWM-cloaked? The EARLIEST dismiss
        /// signal on RT — the ✕/close cloaks it a beat before it fully hides, while the composer still holds focus.
        /// Cheap (one FindWindow + attribute read); used to blur pre-emptively before the OS can re-summon.</summary>
        private static bool IsTabTipCloaked()
        {
            try { IntPtr h = FindWindow("IPTip_Main_Window", null); return h != IntPtr.Zero && IsCloaked(h); }
            catch { return false; }
        }

        /// <summary>KBD-DISMISS-BLUR instrument (KbdDiag-gated): dumps the raw 0.2 dismiss signals + the composite
        /// each armed poll, so the RT log reveals WHICH signal actually flips when the keyboard closes. Read-only.</summary>
        private void LogKbSignals(string tag)
        {
            try
            {
                IntPtr h = FindWindow("IPTip_Main_Window", null);
                bool exists = h != IntPtr.Zero;
                bool vis = exists && IsWindowVisible(h);
                bool cloaked = exists && IsCloaked(h);
                int top = 0, bot = 0, ht = 0; bool onScr = false;
                if (exists && GetWindowRect(h, out NativeRect r))
                {
                    var scr = Screen.FromControl(this).Bounds;
                    top = r.Top; bot = r.Bottom; ht = r.Bottom - r.Top;
                    int edge = Dpi.Px(80, MonitorDpiValue);   // step 3.4: same raw-monitor tolerances as KeyboardShowing
                    onScr = ht >= Dpi.Px(150, MonitorDpiValue) && r.Top < scr.Bottom - edge && r.Bottom > scr.Bottom - edge;
                }
                double occH = 0;
                try { if (WinRtKeyboard.TryOccludedRect(out double _, out double _, out double _, out double oh)) occH = oh; } catch { }
                bool composite = exists && !cloaked && onScr;   // the TabTip "shown" predicate (occludedH>0 covers the InputPane path)
                Logger.Diag("[KBD] sig " + tag + " exists=" + exists + " vis=" + vis + " cloaked=" + cloaked
                    + " rect=(" + top + ".." + bot + " h=" + ht + ") onScreen=" + onScr + " occludedH=" + occH.ToString("0")
                    + " composite=" + composite);
            }
            catch { }
        }

        /// <summary>KBD-DISMISS-v3 PART 1: create the neutral, non-text focus sink EAGERLY and idempotently. It is the
        /// UNCONDITIONAL parking spot for BlurForDismiss — SELECTABLE (so ActiveControl can rest on it and WinForms
        /// restores IT, not the composer, after RestoreFromKeyboard re-maximizes) but NON-TEXT, so it can never summon
        /// the touch keyboard. Off-screen + inert.</summary>
        private void EnsureFocusSink()
        {
            try
            {
                if (_kbFocusSink != null && !_kbFocusSink.IsDisposed) return;
                // A Button IS selectable (ActiveControl can rest on it); a Panel is not. TabStop keeps it a valid
                // active-control target even if WinForms ever falls back to SelectNextControl during the restore.
                _kbFocusSink = new Button { Size = new Size(1, 1), Left = -100, Top = -100, TabStop = true };
                Controls.Add(_kbFocusSink);
            }
            catch { }
        }

        /// <summary>KBD-DISMISS-v3 PART 1: on OSK dismissal, park focus UNCONDITIONALLY on the neutral non-text sink —
        /// so Windows has no focused text field to re-summon the keyboard for, AND so when RestoreFromKeyboard
        /// re-maximizes, WinForms restores the SINK (not the composer) as the active control. That severs the reshow
        /// loop at its root: the composer is never re-focused, so TabTip never fires the synthetic caret-click that
        /// made v2's focus-gate signals (real vs synthetic) identical. Logs where focus landed (the RT proof).</summary>
        private void BlurForDismiss()
        {
            try
            {
                if (IsDisposed) return;
                EnsureFocusSink();
                if (_kbFocusSink != null && !_kbFocusSink.IsDisposed) { try { ActiveControl = _kbFocusSink; } catch { } }
                else ActiveControl = null;   // sink unavailable → the old release primitive (best-effort)
                if (LogOn)
                {
                    Control fc = ActiveControl;
                    string who = fc == null ? "null" : (string.IsNullOrEmpty(fc.Name) ? fc.GetType().Name : fc.Name + "(" + fc.GetType().Name + ")");
                    Logger.Diag("[KBD] keyboard dismissed → blur (parked=" + ReferenceEquals(fc, _kbFocusSink) + "); focus now = " + who);
                }
            }
            catch { }
        }

        /// <summary>KBD-DISMISS-v3.1: a HARDWARE-origin pointer-down (real finger/mouse, provenance originId=hardware —
        /// NOT the dev0/org0 injected click the OS/TabTip fires) landed on a composer/search EDIT. Records it so a
        /// following focus-gain during post-dismiss suppression is recognised as a genuine re-tap and allowed.</summary>
        private void NoteRealEditTouch() { _lastRealEditTouchTick = Environment.TickCount; }

        /// <summary>Is the message being handled now from a real finger/mouse (IMO_HARDWARE), not injected or synthetic?
        /// The same test EditInputProbe makes; false where the API does not exist (before Windows 8).</summary>
        private static bool IsHardwareInputNow()
        {
            try { INPUT_MESSAGE_SOURCE s; return GetCurrentInputMessageSource(out s) && s.originId == 1; }
            catch { return false; }
        }

        /// <summary>KBD-DISMISS-v3.1 (the fix): EditInputProbe calls this on a composer/search WM_SETFOCUS. When NOT
        /// suppressed, inert (normal tap-to-type). While SUPPRESSED (after a dismiss), the focus is allowed ONLY if a
        /// genuine hardware tap on the box landed within RealTouchWindowMs — that ends suppression; otherwise it's the
        /// OS/TabTip re-summon (no hardware provenance) and is bounced back to the sink. State-based, not timed, so the
        /// unbounded 3-5s async re-focus is caught the same as the instant one. Deferred so the pointer-down registers
        /// first; re-checks at run.</summary>
        private void OnTextBoxFocusGain(Control box, string name)
        {
            try
            {
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (IsDisposed || box == null || box.IsDisposed || !box.ContainsFocus) return;
                        if (!InputPolicy.DismissGuard) { _kbSuppressed = false; return; }   // device trip: guard switched off
                        if (Helpers.TouchKeyboard.HasHardwareKeyboard()) return;   // hardware kbd: no OSK to fight
                        if (!_kbSuppressed) return;                                 // normal entry — allow the keyboard
                        int since = unchecked(Environment.TickCount - _lastRealEditTouchTick);
                        bool genuineTap = _lastRealEditTouchTick != 0 && since >= 0 && since <= RealTouchWindowMs;
                        if (genuineTap)
                        {
                            _kbSuppressed = false;   // a real finger re-engaged the box → allow the keyboard, end suppression
                            if (LogOn) Logger.Diag("[KBD] suppression cleared: " + name + " genuine hardware tap " + since + "ms");
                        }
                        else
                        {
                            if (LogOn) Logger.Diag("[KBD] suppressed refocus bounced: " + name + " (no hardware tap; lastRealTouch=" + (_lastRealEditTouchTick == 0 ? "none" : since + "ms") + ")");
                            BlurForDismiss();
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        /// <summary>[KBD] flicker diagnostics: log focus GOT/LOST (outer + inner native EDIT) and keystroke
        /// arrival on a composer/search box — so the RT log shows if focus toggles in a loop and whether
        /// keystrokes reach the box with the WinRT keyboard (vs the legacy keyboard that types fine).</summary>
        private void HookKbdDiag(UI.Controls.InputBox field, string name)
        {
            if (field == null) return;
            if (field.Rich != null)
            {
                // Plan E2: no input shim (it takes WM_CHAR itself). The two dismiss feeds the probe gives an EDIT: a genuine tap
                // re-allows focus during post-dismiss suppression, and a focus gain goes through the same bounce.
                var rich = field.Rich;
                rich.MouseDown += (s, e) => { if (IsHardwareInputNow()) NoteRealEditTouch(); };
                rich.GotFocus += (s, e) => OnTextBoxFocusGain(rich, name + "[Emoji]");
                return;
            }
            if (field.Plain != null)
            {
                // The plain native EDIT (ThemedTextBox.Inner): focus/keystroke diagnostics and the focus probe.
                HookInnerKbdDiag(field.Plain, field.Plain.Inner, name + "[Plain]");
                // A tap on the FRAME around the EDIT never reaches EditInputProbe (it subclasses the EDIT only), so record a
                // genuine finger/mouse tap here — raised before the frame focuses the EDIT — or the post-dismiss suppression
                // bounces that focus straight back to the sink (review 1.5).
                field.Plain.MouseDown += (s, e) => { if (IsHardwareInputNow()) NoteRealEditTouch(); };
            }
        }

        /// <summary>The native-EDIT half of <see cref="HookKbdDiag"/>: focus/keystroke tracing, the IMM heartbeat, the focus probe.</summary>
        private void HookInnerKbdDiag(Control box, Control inner, string name)
        {
            try
            {
                {
                    // Runtime-state snapshot at hook time — one-shot launch breadcrumb (unconditional).
                    var tbState = inner as System.Windows.Forms.TextBoxBase;
                    Logger.Diag("[KBD] STATE " + name + "(inner) ReadOnly="
                        + (tbState != null ? tbState.ReadOnly.ToString() : "?") + " Enabled=" + inner.Enabled);
                    Control innerRef = inner;   // capture for the focus closures
                    inner.GotFocus += (s, e) =>
                    {
                        if (KbdDiag)
                        {
                            Logger.Diag("[KBD] focus " + name + "(inner) GOT");
                            LogEditStyle(innerRef, name + "(inner)", "focus");
                        }
                        LogImeContext(innerRef, name + "(inner)");   // UNCONDITIONAL — the IMM-context heartbeat at focus
                    };
                    inner.LostFocus += (s, e) => { if (KbdDiag) Logger.Diag("[KBD] focus " + name + "(inner) LOST"); };
                    inner.KeyDown += (s, e) => { if (KbdDiag) Logger.Diag("[KBD] " + name + "(inner) keydown " + e.KeyCode); };
                    inner.KeyPress += (s, e) =>
                    {
                        if (KbdDiag) Logger.Diag(
                            "[KBD] " + name + "(inner) keypress " + KeyDesc(((KeyPressEventArgs)e).KeyChar)
                            + " Handled=" + ((KeyPressEventArgs)e).Handled
                            + " focus=" + FocusWhere(box, inner)
                            + " inner.Text=" + Quote(inner.Text));
                    };
                    inner.TextChanged += (s, e) =>
                    {
                        if (KbdDiag) Logger.Diag("[KBD] " + name + "(inner) TextChanged inner.Text=" + Quote(inner.Text));
                    };
                    // The focus probe: tap provenance + the post-dismiss focus bounce (the typing shim is gone, plan E5).
                    AttachInputProbe(inner, name + "(inner)");
                }
            }
            catch { }
        }

        private readonly System.Collections.Generic.List<NativeWindow> _inputProbes = new System.Collections.Generic.List<NativeWindow>();

        /// <summary>Verbose per-keystroke [KBD] tracing gate: requires diagnostic logging ON (Logger.Enabled)
        /// AND the KbdDiag verbosity tier. "Anomaly" lines (forced/corrected) are gated by Logger.Enabled only.</summary>
        private static bool KbdDiag => Logger.Enabled && AppSettings.Instance.KbdDiag;

        /// <summary>Short alias for the app-wide hot-path logging gate (see Helpers.Logger).</summary>
        private static bool LogOn => Logger.Enabled;

        /// <summary>Subclasses a native EDIT's handle with <see cref="EditInputProbe"/> (see the class doc). Handle-create aware
        /// (re-attaches if the EDIT handle is recreated) and released on destroy; kept referenced so it isn't GC'd.</summary>
        private void AttachInputProbe(Control inner, string name)
        {
            if (inner == null) return;
            EditInputProbe current = null;
            void Attach()
            {
                if (!inner.IsHandleCreated) return;
                foreach (var p in _inputProbes) if (p.Handle == inner.Handle) return;   // already hooked this handle
                var pr = new EditInputProbe(inner, name, this);
                try { pr.AssignHandle(inner.Handle); _inputProbes.Add(pr); current = pr; }
                catch { }
            }
            if (inner.IsHandleCreated) Attach();
            inner.HandleCreated += (s, e) => Attach();
            inner.HandleDestroyed += (s, e) =>
            {
                if (current != null) { try { current.ReleaseHandle(); } catch { } _inputProbes.Remove(current); current = null; }
            };
        }

        /// <summary>The focus probe on a native EDIT (the plain search box, and the composer when ComposerEngine is "Plain").
        /// ⚠ THE TYPING SHIM IS GONE (plan E5, 2026-10-14). It forced the keystrokes a "dead" EDIT dropped on RT; the device
        /// trip showed the dead mode came from TelegArm launching TabTip itself (removed, Program.cs), and a plain EDIT types
        /// on its own. What stays is what the touch-keyboard workarounds need: the PROVENANCE of a tap on the EDIT (a real
        /// finger/mouse re-allows focus during post-dismiss suppression; the OS/TabTip's injected re-focus does not), the
        /// focus-gain bounce, and the raw focus/keystroke lines for the log (per-keystroke ones behind KbdDiag).</summary>
        private sealed class EditInputProbe : NativeWindow
        {
            private const int WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;
            private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103,
                              WM_UNICHAR = 0x0109, WM_IME_CHAR = 0x0286,
                              WM_IME_STARTCOMPOSITION = 0x010D, WM_IME_ENDCOMPOSITION = 0x010E, WM_IME_COMPOSITION = 0x010F;
            private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203;
            private const int WM_POINTERDOWN = 0x0246, WM_TOUCH = 0x0240;

            private readonly Control _edit;
            private readonly string _name;
            private readonly Form _owner;
            private bool _needStyleLog;
            private int _lastPtrTick;
            private string _lastPtrLabel = "none";

            public EditInputProbe(Control edit, string name, Form owner) { _edit = edit; _name = name; _owner = owner; }

            // The owning MainForm, or null. ⚠ Never a hard cast: an exception thrown in a NativeWindow's WndProc is swallowed
            // silently (NativeWindow.OnThreadException is empty), and the message with it.
            private MainForm Main => _owner as MainForm;

            /// <summary>The current input message's provenance: device (touch/mouse/pen) + origin (hardware = a real finger,
            /// injected/system = the OS/TabTip's synthetic re-focus) + sent/posted.</summary>
            private static string InputSrc(out bool hardware)
            {
                hardware = false;
                try
                {
                    INPUT_MESSAGE_SOURCE s;
                    if (GetCurrentInputMessageSource(out s))
                    {
                        hardware = s.originId == 1;
                        string dev = s.deviceType == 4 ? "touch" : s.deviceType == 2 ? "mouse" : s.deviceType == 8 ? "pen"
                                   : s.deviceType == 1 ? "kbd" : s.deviceType == 16 ? "touchpad" : "dev" + s.deviceType;
                        string org = s.originId == 1 ? "hardware" : s.originId == 2 ? "injected" : s.originId == 4 ? "system" : "org" + s.originId;
                        return dev + "/" + org + (InSendMessage() ? "/sent" : "/posted");
                    }
                }
                catch { }
                return "src?";
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK || m.Msg == WM_POINTERDOWN || m.Msg == WM_TOUCH)
                {
                    string kind = m.Msg == WM_LBUTTONDOWN ? "LBDOWN" : m.Msg == WM_LBUTTONDBLCLK ? "LBDBLCLK"
                                : m.Msg == WM_POINTERDOWN ? "POINTERDOWN" : "TOUCH";
                    bool hardware; _lastPtrTick = Environment.TickCount; _lastPtrLabel = kind + " " + InputSrc(out hardware);
                    if (hardware) Main?.NoteRealEditTouch();   // genuine finger/mouse → re-allow focus during suppression
                    if (LogOn) Logger.Diag("[KBD] PTR " + _name + " " + _lastPtrLabel);
                }
                if (m.Msg == WM_CHAR && Main != null) Main._lastComposerKeyTick = Environment.TickCount;   // KBD-CLOSE-PROBE

                if (m.Msg == WM_SETFOCUS)
                {
                    if (LogOn) Logger.Diag("[KBD] RAW " + _name + " WM_SETFOCUS otherHwnd=0x" + m.WParam.ToInt64().ToString("X")
                        + " lastPtr=" + (_lastPtrTick == 0 ? "never" : unchecked(Environment.TickCount - _lastPtrTick) + "ms[" + _lastPtrLabel + "]") + " srcNow=" + InputSrc(out _));
                    _needStyleLog = true;
                    base.WndProc(ref m);
                    Main?.OnTextBoxFocusGain(_edit, _name);   // KBD-DISMISS-v3: re-park a post-dismiss refocus
                    return;
                }
                if (m.Msg == WM_KILLFOCUS)
                {
                    if (LogOn) Logger.Diag("[KBD] RAW " + _name + " WM_KILLFOCUS otherHwnd=0x" + m.WParam.ToInt64().ToString("X"));
                    base.WndProc(ref m);
                    return;
                }
                if (KbdDiag && IsTracked(m.Msg))
                {
                    if (_needStyleLog) { _needStyleLog = false; LogEditStyle(_edit, _name, "firstchar"); }
                    LogRaw(ref m);
                }
                base.WndProc(ref m);
            }

            private static bool IsTracked(int msg)
            {
                switch (msg)
                {
                    case WM_KEYDOWN: case WM_KEYUP: case WM_CHAR: case WM_DEADCHAR: case WM_UNICHAR:
                    case WM_IME_CHAR: case WM_IME_STARTCOMPOSITION: case WM_IME_ENDCOMPOSITION: case WM_IME_COMPOSITION:
                        return true;
                    default: return false;
                }
            }

            private void LogRaw(ref System.Windows.Forms.Message m)
            {
                uint lp = (uint)(m.LParam.ToInt64() & 0xFFFFFFFF);
                int rep = (int)(lp & 0xFFFF), scan = (int)((lp >> 16) & 0xFF), ext = (int)((lp >> 24) & 1);
                int ctx = (int)((lp >> 29) & 1), prev = (int)((lp >> 30) & 1), up = (int)((lp >> 31) & 1);
                bool self = _owner != null && _owner.IsHandleCreated && GetForegroundWindow() == _owner.Handle;
                Logger.Diag("[KBD] RAW " + _name + " " + MsgName(m.Msg) + " " + WParamDesc(m.WParam)
                    + " lParam=0x" + lp.ToString("X8") + " rep=" + rep + " scan=0x" + scan.ToString("X2")
                    + " ext=" + ext + " ctx=" + ctx + " prev=" + prev + " up=" + up
                    + " mods=" + ModSnapshot()
                    + " act=" + (GetActiveWindow() != IntPtr.Zero) + " fg=" + (self ? "self" : "other")
                    + " origin=" + InputOrigin() + " sent=" + InSendMessage());
            }
        }

        /// <summary>Message-name label for the [KBD] RAW line.</summary>
        private static string MsgName(int msg)
        {
            switch (msg)
            {
                case 0x0100: return "WM_KEYDOWN";
                case 0x0101: return "WM_KEYUP";
                case 0x0102: return "WM_CHAR";
                case 0x0103: return "WM_DEADCHAR";
                case 0x0109: return "WM_UNICHAR";
                case 0x0286: return "WM_IME_CHAR";
                case 0x010D: return "WM_IME_STARTCOMPOSITION";
                case 0x010E: return "WM_IME_ENDCOMPOSITION";
                case 0x010F: return "WM_IME_COMPOSITION";
                default: return "0x" + msg.ToString("X");
            }
        }

        /// <summary>wParam as hex, with the printable char if its low word is one — "'h'(0x68)" or "(0x08)".</summary>
        private static string WParamDesc(IntPtr wParam)
        {
            int w = (int)(wParam.ToInt64() & 0xFFFF);
            char c = (char)w;
            return (c >= ' ' && c < (char)0x7F) ? "'" + c + "'(0x" + w.ToString("X2") + ")" : "(0x" + w.ToString("X2") + ")";
        }

        /// <summary>Modifier snapshot from GetKeyState at message time: S/C/A/W, uppercase = down (e.g. "-C--").</summary>
        private static string ModSnapshot()
        {
            bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;
            return (Down(VK_SHIFT) ? "S" : "-") + (Down(VK_CONTROL) ? "C" : "-")
                 + (Down(VK_MENU) ? "A" : "-") + ((Down(VK_LWIN) || Down(VK_RWIN)) ? "W" : "-");
        }

        /// <summary>Logs the EDIT's window style + ES_READONLY flag (H3'/persistent-state check).</summary>
        private static void LogEditStyle(Control edit, string name, string when)
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting when logging is off
                if (edit == null || !edit.IsHandleCreated) return;
                int style = GetWindowLong(edit.Handle, GWL_STYLE);
                Logger.Diag("[KBD] STYLE " + name + " [" + when + "] style=0x" + style.ToString("X8")
                    + " ES_READONLY=" + (((style & ES_READONLY) != 0) ? "yes" : "no") + " Enabled=" + edit.Enabled);
            }
            catch { }
        }

        /// <summary>Input-source label from GetCurrentInputMessageSource (Win8+): which keyboard produced the
        /// current message — HARDWARE / INJECTED / SYSTEM (IMO_* originId), or n/a if unavailable.</summary>
        private static string InputOrigin()
        {
            try
            {
                if (GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE src))
                {
                    switch (src.originId)   // IMO_HARDWARE=1, IMO_INJECTED=2, IMO_SYSTEM=4
                    {
                        case 1: return "HARDWARE";
                        case 2: return "INJECTED";
                        case 4: return "SYSTEM";
                    }
                }
            }
            catch { }
            return "n/a";
        }

        /// <summary>Part 1.5: at the moment the TabTip poll flips to SHOW, snapshot the TabTip window's visibility
        /// + rect and the current foreground window class — to catch a false-SHOW / flicker (observation only).</summary>
        private void LogShowCheck()
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting (or window probing) when off
                IntPtr tt = FindWindow("IPTip_Main_Window", null);
                bool vis = tt != IntPtr.Zero && IsWindowVisible(tt);
                NativeRect r = new NativeRect();
                if (tt != IntPtr.Zero) GetWindowRect(tt, out r);
                IntPtr fg = GetForegroundWindow();
                var sb = new System.Text.StringBuilder(96);
                if (fg != IntPtr.Zero) GetClassName(fg, sb, sb.Capacity);
                Logger.Diag("[KBD] SHOW-check tabtipVis=" + vis
                    + " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + " fg=" + sb.ToString());
            }
            catch { }
        }

        /// <summary>KBD-CLOSE-PROBE (instrument only): the class of the current foreground window ("0"/"?" on failure).</summary>
        private static string FgClass()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return "0";
                var sb = new System.Text.StringBuilder(96);
                GetClassName(fg, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return "?"; }
        }

        /// <summary>KBD-CLOSE-PROBE (instrument only): dump the TabTip window's visual state + who holds focus each armed
        /// tick, but ONLY when it CHANGES (no 120ms spam). Around a foreground ✕ tap this shows whether TabTip
        /// moves/shrinks/cloaks BEFORE our HIDE detection and whether the composer still holds focus at that instant —
        /// i.e. is there an early signal we could hook to blur pre-emptively before the OS re-summons. No behavior change.</summary>
        private void LogTabTipTick()
        {
            if (!Logger.Enabled) return;   // hot-path: zero window-probing when logging is off
            try
            {
                IntPtr tt = FindWindow("IPTip_Main_Window", null);
                bool exists = tt != IntPtr.Zero;
                bool vis = exists && IsWindowVisible(tt);
                bool cloaked = exists && IsCloaked(tt);
                NativeRect r = new NativeRect();
                if (exists) GetWindowRect(tt, out r);
                bool composerFocus = _messageInput != null && _messageInput.ContainsFocus;
                bool searchFocus = _searchBox != null && _searchBox.ContainsFocus;
                bool typing = _lastComposerKeyTick != 0 && unchecked(Environment.TickCount - _lastComposerKeyTick) < 1200;
                string state = "vis=" + vis + " cloaked=" + cloaked
                    + " rect=" + r.Left + "," + r.Top + "," + (r.Right - r.Left) + "x" + (r.Bottom - r.Top)
                    + " fg=" + FgClass() + " composerFocus=" + composerFocus + " searchFocus=" + searchFocus + " typing=" + typing;
                if (state != _ttLast) { _ttLast = state; Logger.Diag("[KBD] tabtip-tick " + state); }
            }
            catch { }
        }

        /// <summary>Logs whether an IME context is attached to the EDIT (immersive keyboard on-show diversion — H3').</summary>
        private static void LogImeContext(Control edit, string name)
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting (or IMM calls) when logging is off
                if (edit == null || !edit.IsHandleCreated) return;
                IntPtr himc = ImmGetContext(edit.Handle);
                Logger.Diag("[KBD] IME " + name + " imeCtx=" + (himc == IntPtr.Zero ? "null" : "0x" + himc.ToInt64().ToString("X")));
                if (himc != IntPtr.Zero) ImmReleaseContext(edit.Handle, himc);
            }
            catch { }
        }

        /// <summary>Does this Enter keypress mean SEND, under the user's chosen mode?
        /// ⚠ The modifier must be EXACT: in Enter mode, Ctrl+Enter must NOT send, or someone who switched
        /// back from Ctrl mode has their muscle memory firing messages silently. An unrecognised stored
        /// value falls back to plain Enter — the default — rather than leaving the composer unable to
        /// send at all, which is what a strict enum parse would have done to a hand-edited settings file.</summary>
        internal static bool SendKeyPressed(KeyEventArgs e)   // internal: the editor window (2.7d) uses the same rule
        {
            switch (AppSettings.Instance.SendKey)
            {
                case "CtrlEnter": return e.Control && !e.Alt;
                case "AltEnter": return e.Alt && !e.Control;
                default: return !e.Control && !e.Alt;   // "Enter"
            }
        }

        /// <summary>[KBD] SEND truth: logs the composer's text length + preview from a send entry point, so an empty send
        /// (text-capture bug) is distinguishable and a missing "button" line pinpoints a hit-test bug.</summary>
        private void SendPathLog(string via)
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting when logging is off
                string outer = _messageInput != null ? (_messageInput.Text ?? "") : "";
                Control inner = _messageInput == null ? null
                    : _messageInput.Plain != null ? _messageInput.Plain.Inner
                    : (Control)_messageInput.Rich;
                string innerT = inner != null ? (inner.Text ?? "") : "";
                Logger.Diag("[KBD] SEND via=" + via + " outer.len=" + outer.Length
                    + " inner.len=" + innerT.Length + " text=" + Quote(outer));
            }
            catch { }
        }

        /// <summary>'h' for a printable char, "#13" for a control char — compact keypress label for the [KBD] log.</summary>
        private static string KeyDesc(char c) => char.IsControl(c) ? "#" + (int)c : "'" + c + "'";

        /// <summary>Short single-quoted preview of a text buffer (truncated) for the [KBD] log — so an empty box
        /// reads as '' and a filled one shows its content.</summary>
        private static string Quote(string t)
        {
            if (t == null) return "<null>";
            if (t.Length > 24) t = t.Substring(0, 24) + "…";
            return "'" + t + "'";
        }

        /// <summary>Names which HWND currently holds the thread's Win32 keyboard focus, relative to a composer:
        /// "inner" (the real EDIT — WM_CHAR inserts), "outer" (the ThemedTextBox host — WM_CHAR is dropped),
        /// or "other/0x…" (something else). This is the crux of the char-not-captured diagnosis.</summary>
        private static string FocusWhere(Control outer, Control inner)
        {
            try
            {
                IntPtr f = GetFocus();
                if (inner != null && inner.IsHandleCreated && f == inner.Handle) return "inner";
                if (outer != null && outer.IsHandleCreated && f == outer.Handle) return "outer";
                return "other(0x" + f.ToInt64().ToString("X") + ")";
            }
            catch { return "?"; }
        }

        /// <summary>After a NON-click reactivation (WA_ACTIVE) WinForms auto-restores focus to the last text
        /// field (composer/search); Windows then auto-reshows the touch keyboard for it → the close→reshow loop
        /// (and the keyboard popping up at startup). Clearing that auto-focus keeps the keyboard closed unless
        /// the USER taps a text field. Runs deferred (BeginInvoke) so it's after WinForms' focus restoration.</summary>
        private void ClearAutoRefocusedTextField()
        {
            try
            {
                if (IsDisposed) return;
                if ((_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus))
                {
                    if (LogOn) Logger.Diag("[KBD] cleared auto-refocused text field after WA_ACTIVE (no keyboard auto-reshow)");
                    ActiveControl = null;   // established pattern in this form; removes focus → the OSK won't auto-reshow
                }
            }
            catch { }
        }

        /// <summary>True when a WM_ACTIVATEAPP=False is the touch keyboard STEALING activation (we're still the
        /// real foreground app) rather than the app going to background — so the WebM pause/flicker churn is
        /// skipped. Signals: a composer/search text field still has focus (the steal fires before focus is
        /// lost), OR the new foreground is ours / the keyboard / an immersive input surface. A genuine app
        /// switch (another process, a normal window) → false (real deactivation, pause as before).</summary>
        private bool IsSpuriousDeactivation()
        {
            try
            {
                if ((_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus)) return true;
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) return true;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == GetCurrentProcessId()) return true;   // a window of our OWN process (menu/child/popup)
                var sb = new System.Text.StringBuilder(96);
                GetClassName(fg, sb, sb.Capacity);
                string cls = sb.ToString();
                if (LogOn) Logger.Diag("[KBD] deactivation fg-class=" + cls);
                if (cls.IndexOf("IPTip", StringComparison.OrdinalIgnoreCase) >= 0                    // legacy TabTip
                    || cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)  // immersive input surface
                    || cls.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
        }

    }
}
