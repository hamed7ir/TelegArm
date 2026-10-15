<!-- DRAFT: ships only after Appendix A passes on the Surface RT (gates G2 and G3, device half). -->
# TelegArm v2.0.0

A Telegram desktop client for **Windows RT 8.1 / Windows 10 ARM32** — and x86/x64 — built because no
modern Telegram client supports those devices any more.

Everything since **v1.9.0**. The 2.0 line is a redesign: TelegArm now draws its own windows instead of
using MaterialSkin, typing on the Surface RT works, and every window is sharp at any display scale.

---

## A new look

- **TelegArm's own window frame.** Every window has its own title bar with minimise, maximise and close,
  Windows 11 rounded corners, and edges wide enough to resize by touch. MaterialSkin is gone, and with it
  the re-skinning of every window each time a dialog opened.
- **Choose an accent colour** in Settings › Appearance, or keep following the Windows accent. Light, dark
  and follow-Windows themes still apply live.
- **Settings, rebuilt** as pages: General, Appearance, Notifications, Chats & Composer, Data & Storage,
  Connection, Privacy, Security, Devices, Advanced and About.
- **Dialogs, rebuilt**, keeping their keyboard habits: Enter on "Delete / Cancel" is still Cancel, Esc
  closes, Ctrl+C copies the whole message. A destructive action is drawn red, and a long message scrolls.
- Restyled scrollbars and context menus, with touch-sized rows and icons.
- The proxy pill moves into the title bar. The main window has an empty state, and avatar initials are
  centred.
- The media viewer is immersive and resizable, with its bars under the video instead of over it. The login
  screen has rounded fields and a back button in the title bar.
- **When I close the window** (Settings › General): minimise to the tray, ask, or exit.

## Typing on the Surface RT

On the RT, typing used to die in every text box. The cause turned out to be TelegArm starting the touch
keyboard itself; Windows now opens it, and text boxes stay alive.

- **The colour-emoji composer is the default.** Emoji are drawn in colour as you type. Backspace removes a
  whole emoji, Shift+Enter starts a new line, Ctrl+arrows move by word, and Ctrl+Z / Ctrl+Y undo and redo.
- The composer **grows with its text** up to 13 lines and keeps the chat scrolled to the bottom.
- **Edit in a window:** the expand button on the composer (or its menu) opens a larger editor with Bold,
  Italic, Strikethrough, Monospace, Link, emoji, Undo / Redo, Clear and Send. Esc keeps your text.
- The plain Windows text box is still available in Settings › Advanced › Composer.

## Sharp at any display scale

- **Every window and control scales with the display.** At 150% the chat list, header, rows and composer
  used to keep their 100% sizes while the text grew; now everything is laid out for the window's DPI. At
  100% (the Surface RT) nothing moves.
- **Per-monitor DPI by default.** Moving a window to a monitor with another scale resizes it properly.
  Menus, notifications, the emoji picker, the downloads panel, the pinned-message list, poll dialogs, the
  story viewer and animated stickers are all sized for the monitor they appear on.
- **Interface scale:** a slider in Settings › Appearance › Size, 50–200% in 25% steps. It applies live to
  every window, with no restart. A "Keep this size?" question follows, and the size reverts after 10
  seconds if you don't answer. **Ctrl+=** / **Ctrl+-** change the scale and **Ctrl+0** resets it. On a small
  screen the scale is limited so the main window still fits (about 150% on a Surface RT).
- **Display scaling mode** (Settings › Advanced): Automatic, System, or Compatibility (Windows stretches
  the app: blurry, but always proportional) — a fallback if a display misbehaves.

## Safer installs and settings

- **A single Setup.exe** carries the app inside it and checks every file against its SHA-256 as it writes
  it. It starts even without .NET 4.7, so it can tell you what is missing.
- A running TelegArm blocks install and uninstall with a retry prompt; it is never killed. Uninstalling
  keeps your accounts and settings unless you ask otherwise.
- **Settings are saved atomically**, so a crash mid-save can no longer truncate them. A corrupt settings
  file is kept aside rather than overwritten. Settings from 1.x carry over, and the old file is kept as
  `settings.v1.9.json`.

## Fixes

- Persian and emoji text no longer randomly render bold (2 launches in 10 did).
- The attach and bot menus sit fully above their buttons instead of overlapping them.
- The pinned-message list no longer leaves a gap or shows a stray horizontal scrollbar.
- The downloads icon in the chat header lines up with the search and menu icons.

## For testers

- **Settings › Advanced › Copy diagnostics** copies the version, Windows build, DPI and connection mode
  for a bug report.
- **Ctrl+Alt+Shift+D** shows a small DPI overlay: the window's DPI, the scale, the mode, the monitor, and a
  100-pixel ruler.
