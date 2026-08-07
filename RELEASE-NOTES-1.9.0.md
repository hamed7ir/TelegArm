# TelegArm v1.9.0

A Telegram desktop client for **Windows RT 8.1 / Windows 10 ARM32** — and x86/x64 — built because no
modern Telegram client supports those devices any more.

Everything since **v1.1.0**.

---

## Reaching Telegram on a blocked network

**MTProxy support.** Paste a `tg://proxy` or `t.me/proxy` link and it opens *in the app* with a
confirmation sheet instead of bouncing to a browser. Three entry points: the link, the settings
page, and the connection pill.

- **Applies live** — switching proxy no longer needs a restart, and the warm connection pool is torn
  down and rebuilt safely around the switch.
- **Real handshake testing**, not a ping. "Test" performs an actual MTProto handshake, so a server
  that accepts TCP but rejects your secret is reported as a **probable wrong secret** rather than as
  working. Measured: good proxies 3.6–8.4 s, a bogus host fails in 207 ms, a wrong secret at ~3.3 s.
- **Share and QR code** for any saved proxy.
- Links are validated at paste; your secret is never written to the log.

## Notifications, rebuilt

This was the largest piece of work in the release, and most of it was fixing things that were quietly
wrong.

- **No more burst on launch.** Starting after a few hours offline used to fire a toast for every
  message you missed. Messages that predate the run no longer notify — what you missed is carried
  better by the unread badges and the chat list.
- **Muted chats are honoured.** The mute check used to consult a list that only holds the first ~100
  chats, so a muted conversation further down notified anyway. It now asks the account's real notify
  state, and when it cannot answer it stays **silent** rather than guessing.
- **Mentions are detected properly.** Telegram's "you were mentioned" flag turns out not to be set
  for every message that names you, so mentions in a muted chat could go silent. TelegArm now works
  it out itself — from the message's entities and your own username — and a mention still breaks
  through a muted chat.
- **Silent messages stay silent.** Messages a sender marked "no notification" no longer ping. They
  still count toward your unread badge.
- **A real notification window** instead of a tray balloon: your own theme, the chat's avatar, click
  to open the conversation, hover to stop it disappearing.
  ⚠ **It never steals focus.** If you are typing when one appears, not one character is lost.
- **A stack of up to three**, with the rest queued. Forty messages from one busy group produce **one**
  notification with a count, not forty windows.
- **Master mute** — one switch silences everything, mentions included. Unread badges keep counting.
- **Action Center entries on Windows 10/11** (see the limitation below), added silently alongside the
  window and retired when you read the chat.
- **A taskbar unread badge**, which works everywhere including Windows RT — and which counts only
  chats you have **not** muted.
- **Preview privacy**: one setting hides message text from the notification, the Action Center entry
  and the tile together.

## Interface

- **A right-side dock** with Info and Emoji panes. The Info pane is the full profile — not a summary
  — and the Emoji pane is the composer's own panel, with stickers and GIFs.
- **A round send button** with a paper-plane glyph, replacing a button labelled "Send".
- **A ⋮ menu in the chat header**, offering the same actions as the chat list.
- **Folder pinning** — pin a chat within a folder.
- Emoji in the chat list and in your own just-sent messages render correctly instead of showing
  boxes, and the emoji grid repaints ~8x faster.

## Correctness

- **Folder members that were never loaded now appear.** A folder could look empty or short because
  its chats had not been paged in yet; they are now fetched.
- **Account integrity.** The two-client session collision that could corrupt a session file — and in
  the worst case log an account out — is closed. Corrupt-session recovery **never deletes an
  account**; it retries, then moves the folder aside.
- **Licence and notices now ship with the app.** Until v1.1.0 the installers shipped neither.
- **Settings no longer revert.** If a setting was rejected on save, every *other* setting on that
  screen applied immediately and then silently reverted on the next launch. Fixed.

## Installing

- **Installer** (`TelegArm-1.9.0-Setup-AnyCPU.zip`) — runs on RT/ARM32 and on x86/x64. It now checks
  for **.NET Framework 4.7** and, if it is missing, tells you where to get it **for your
  architecture** and stops, instead of installing something that cannot start.
  ⚠ On ARM32 use the Open-RT mirror — Microsoft's web installer does not provide an ARM32 build.
- **Portable** — unzip and run. See `PORTABLE-README.txt`. It cannot check for the runtime itself,
  and it gets no Action Center entry or Start tile (both need an installed shortcut); the
  notification window and the taskbar badge work normally.

---

## ★ Known limitations

Stated because you will otherwise find them yourself.

- **★ Action Center entries are not confirmed working end-to-end.** The installer does stamp the
  required app identity onto the Start-Menu shortcut — verified by reading it back off a real
  install — but on the Windows 11 development machine Windows still did not accept that identity for
  notification delivery, so no Action Center entry could be observed. Everything degrades correctly:
  the notification **window** appears exactly as normal, and the app writes one log line explaining
  why there is no history (`[SHELL] Action Center OFF — …`). Treat Action Center as **unverified** in
  this release rather than as a working feature.
- **The taskbar unread badge undercounts.** It sums the chats currently loaded — roughly the first
  page — so unread conversations further down the list are not counted until you scroll. The badge
  deliberately ignores muted chats.
- **There is no live tile, and there cannot be one.** It was attempted and dropped. An unpackaged
  desktop app's Start tile is a *static shortcut tile* — Windows accepts tile updates for it and
  silently discards them. The giveaway is that a pinned TelegArm tile offers only Small and Medium;
  Wide and Large exist only for apps that declare them in a UWP manifest. Supporting it would require
  packaging TelegArm as a sparse MSIX with a signing certificate, and Windows 11 has removed tiles
  altogether. The taskbar unread badge covers the same need and works everywhere.
- **The notification window's no-focus-steal behaviour is verified on Windows 11, not yet on 8.1.**
  If it misbehaves, Settings has a "legacy tray balloon" switch that restores the old behaviour
  without reinstalling.
- **High-DPI is only partly verified.** Notification layout is measured at 125% and computed for
  150%/200%. **The main app window itself does not yet scale to high DPI** — a known gap, not new in
  this release.
- **Folder pinning is unavailable in shared/imported chatlist folders**, because Telegram does not
  allow it there.
- A Windows 7 machine with only .NET 4.0 cannot run the installer at all (it needs 4.5 to run and
  4.7 to install).
