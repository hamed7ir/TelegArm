# TelegArm v2.0.0

A Telegram desktop client for **Windows RT 8.1 / Windows 10 ARM32** — and x86/x64 — built because no
modern Telegram client supports those devices any more.

Everything since **v1.9.0**. The 2.0 line is a redesign: TelegArm now draws its own windows instead of
using MaterialSkin, typing on the Surface RT works, every window is sharp at any display scale — and
**Telegram calls, video calls and voice chats** work, on the Surface RT too.

---

## Calls, video calls and voice chats

- **Voice calls.** Call a person from the phone in the chat header. Incoming calls ring in a call window
  with Accept and Decline; a call shows its time, signal and mute. Closing the window hangs up.
- **Video calls.** Right-click (or press and hold) the phone, or use the chat menu: *Video call*. Start or
  stop video during any call, switch between the front and back cameras, and see the other side full-window
  with your own picture in the corner. On the Surface RT (Windows 8.1) the camera works through a media sink
  of our own; on Windows 10 through the frame reader.
- **Voice chats.** In a group or channel with a voice chat on, the header shows a voice-chat button: join
  (muted, as in Telegram Desktop), unmute, raise your hand when an admin muted you, and leave. Admins can
  start a voice chat and end it for everyone. The window lists who is there, with a ring that follows who
  is speaking.
- Telegram's own call library (tgcalls and its WebRTC, the pair Telegram Desktop 7.3.1 ships), built for
  ARM32 and x86. The key exchange is checked as Telegram Desktop checks it. Echo cancellation, noise
  suppression and gain control are on. A call and a voice chat exclude each other; a second incoming call
  is answered busy.
- Calls need Windows 8.1 or later (not Windows 7).

## A new look

- **TelegArm's own window frame.** Every window has its own title bar with minimise, maximise and close,
  Windows 11 rounded corners, and edges wide enough to resize by touch. MaterialSkin is gone, and with it
  the re-skinning of every window each time a dialog opened.
- **Choose an accent colour** in Settings › Appearance — eight fixed ones, any colour from a picker, or
  the Windows accent. Light, dark and follow-Windows themes still apply live.
- **Theme colours:** the base of the light theme (white, warm white, sepia, cool grey, mint or your own)
  and of the dark theme (graphite, true black, blue-grey, midnight, warm dark or your own).
- **Readable with any accent:** text on the accent switches to dark ink for light accents such as yellow.
- **Chat backgrounds** per theme: built-in ones, a solid colour, a gradient, a pattern or a photo; outgoing
  bubbles from the accent or a colour of your own; export and import a theme.
- **Wallpaper window background** (optional): a blurred, tinted copy of your desktop picture behind the
  window, dialogs, menus and Settings.
- **True round corners** on Windows 10 and 11, with no bars down the sides.
- **One column** below 800 px, as in Telegram Desktop: the chat list, or the open chat with a back arrow.
  The window can be as narrow as 380 px, and the Surface RT in portrait gets it.
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
- **Right-to-left text:** a paragraph reads right to left when its first real letter is Persian, Arabic or
  Hebrew (digits and emoji don't decide it), in the composer and in sent messages; the arrow keys move the
  way the line reads.

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

## Media

- **FFmpeg replaces libVLC** for all audio and video (FFmpeg 8.1.2, ARM32 with NEON). Nothing is extracted on
  first run any more, and the download is about 85 MB smaller. Voice notes and music at 1.5x and 2x keep
  their pitch; MP3, Vorbis, old phone videos, ALAC and more WAV formats play.
- **Round videos** on Windows 10 preview the camera live at about 30 fps and are cropped to the centre
  square; the front camera's preview is mirrored. (Windows 7 can no longer record round videos.)

## Connection

- **Built-in WebSocket tunnel** (off by default): reach Telegram through Telegram Web's gateway — the route
  tg-ws-proxy takes — with nothing to install. Settings › Connection has strict mode, gateway addresses and
  a probe; the Proxy dialog can test it.
- **SOCKS5 proxies:** t.me/socks and tg://socks links can be pasted, tapped, shared, tested and used.
- Switching proxy moves running media downloads to the new route.
- WTelegramClient 4.4.9.

## Telegram features

- Scheduled messages (right-click or hold the send button), the full reaction grid, "Who reacted" and
  "Seen by…", translation (when Telegram offers it), quizzes, auto-delete timers, invite links that expire
  or have a member limit, adding a contact by phone number.
- Folders: create, edit, move and delete them. Search inside a chat by photos, videos, files, links or voice
  messages; limit the global search to private chats, groups or channels.
- Privacy exceptions (always / never allow), and changing a privacy setting no longer erases them.
- Mute for 1 hour, 8 hours, 2 days or forever; jump through unread mentions and reactions; copy a message
  link; report a message; forward without the sender's name or captions; add sticker and emoji packs from
  links; save and search GIFs; slow-mode countdown; underline, spoiler and quote formatting; send without a
  link preview.
- Locations show the place with an "Open in map" link; calls, video chats, topics and other events are
  described in words.

## Keyboard, windows and accessibility

- The main window reopens where it was closed, at the same size, even across monitors of different scale.
- Shortcuts: Alt+Up/Down (previous/next chat), Ctrl+Tab and Ctrl+1..9 (folders), Ctrl+E (edit your last
  message), Ctrl+Shift+M (mute), Ctrl+U (underline); Esc steps back one layer at a time. The list is in
  Settings › Advanced.
- Screen readers get names for the header and composer buttons and the text boxes; drawn rows and bubbles
  are no longer empty Tab stops; Left / Right step through an album in the media viewer.

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
- Pause, Resume and Cancel in the downloads list act at once, even when the network doesn't answer.
- The emoji picker and the downloads panel no longer crash on open; the reaction grid opens on its message.
- After a long time away, old messages no longer stream into the chat list one by one.

## For testers

- **Settings › Advanced › Copy diagnostics** copies the version, Windows build, DPI and connection mode
  for a bug report.
- **Ctrl+Alt+Shift+D** shows a small DPI overlay: the window's DPI, the scale, the mode, the monitor, and a
  100-pixel ruler.

## Source and licenses

TelegArm is GPL-3.0-only. The call library (`tgcalls_native.dll`) is built from the source snapshot
attached to this release (`tgcalls-sources-20261016.zip`); its patches and build scripts are in
`third-party/tgcalls/`. THIRD-PARTY-NOTICES.txt lists every bundled component with its license.
