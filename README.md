# TelegArm

**A full-featured Telegram client for the tablets Telegram forgot.**
<img width="1280" height="704" alt="photo_2026-07-08_05-25-42" src="https://github.com/user-attachments/assets/7238ec54-7fa1-4e13-b380-cda772dc1424" />

TelegArm brings a modern Telegram experience to jailbroken **Windows RT 8.1** and **Windows 10 ARM32** — hardware the official apps never supported and everyone else gave up on. It's written in pure-managed C# and speaks the MTProto protocol directly through [WTelegramClient](https://github.com/wiz0u/WTelegramClient), with zero native Telegram dependencies. One `AnyCPU` build runs everywhere: from a 2012 Surface RT to a modern x64 desktop.

No TDLib, no native Telegram layer to cross-compile, no per-architecture builds to juggle — just a single 32-bit-clean binary that happens to also run on the last hardware anyone expected Telegram to reach.

---

## Runs on

| Platform | Status |
|---|---|
| **Windows RT 8.1 (ARM32)** — original Surface / Surface 2 | ✅ Supported *(requires the RT jailbreak to run desktop apps)* |
| **Windows 10 ARM32** | ✅ Supported |
| Windows 8.1 / 10 / 11 (x86 / x64 / ARM64) | ✅ Supported |
| Windows 7 | ⚠️ Untested |

A single `AnyCPU + Prefer32Bit` binary covers all of the above (runs as a 32-bit process on every architecture).

---

## Features

**Messaging**
- Phone + 2FA login with silent session resume, QR login, and multiple accounts
- Live chat list and conversations with bidirectional paging and combined search (chats + global messages)
- Reply (quoted), forward, copy, multi-select, delete (for me / for everyone), and retry
- Pinned messages, folders, reactions, polls, inline keyboards & bots, and group/channel admin

**Media**
- RTL-aware message bubbles (Persian / Arabic), custom emoji, and animated stickers (`.tgs` via rlottie)
- Photos with thumbnails, disk cache, and a zoom/pan viewer
- Inline video & GIF playback (bundled libVLC) with a system-player fallback
- Documents as file cards — open, Save / Save As — with background downloads (pause / resume / a full download manager)
- Send media by attach or drag-and-drop: Compress or Send-as-file, captions, per-file progress, correct dimensions and poster thumbnails
- Record and send voice notes (OGG/Opus, pure-managed and ARM32-safe) and round video notes
- Configurable media auto-download and a managed cache folder

**Interface**
- System / Light / Dark theming that tracks the Windows accent color **live**, no restart
- Chat folders as tabs or a side panel
- Touch panning, momentum scrolling, and full on-screen-keyboard handling for tablet use
- Notifications with server-synced mute

---

## Building

**Requirements:** Visual Studio 2017, .NET Framework 4.7, C# 7.3.

1. **Clone the repo.**

2. **Provide your own Telegram API credentials.** They're deliberately kept out of source control:
   - Register an app at [my.telegram.org/apps](https://my.telegram.org/apps) to get an `api_id` and `api_hash`.
   - In `TelegArm/Core/`, copy `ApiCredentials.Local.cs.example` → `ApiCredentials.Local.cs` and fill in your values.
   - **This file is gitignored and must never be committed.** Without it the project still builds (placeholders in `ApiCredentials.cs`), but it won't authenticate.

3. **Build the solution** (Debug or Release, `AnyCPU`). NuGet restores the dependencies automatically.

Everything the app needs at runtime — the bundled libVLC (per-architecture), rlottie, Noto Emoji, and fonts — ships with the project and is copied next to the executable on build. No manual codec or DLL setup required.

---

## License

TelegArm is released under the **GNU General Public License v3.0 only** (GPL-3.0-only) — see [LICENSE](https://github.com/hamed7ir/TelegArm/blob/main/LICENSE).

Bundled third-party components (WTelegramClient, MaterialSkin.2, LibVLCSharp / libVLC, Newtonsoft.Json, NAudio, Concentus, SixLabors.ImageSharp, QRCoder, rlottie, and the Roboto / Vazirmatn / Noto Emoji assets) remain under their own licenses — see [THIRD-PARTY-NOTICES.txt](https://github.com/hamed7ir/TelegArm/blob/main/THIRD-PARTY-NOTICES.txt).

---

## Credits

Built on [WTelegramClient](https://github.com/wiz0u/WTelegramClient). Developed with assistance from Claude (Anthropic).
Thanks to Wizou for his contributions and his amazing library WTelegramClient.


