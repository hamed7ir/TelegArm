# TelegArm
<img width="1366" height="768" alt="te" src="https://github.com/user-attachments/assets/46279785-a7f0-4d54-8f87-00184b12107b" />

A lightweight, **pure-managed C# Telegram client** built to run on **jailbroken Windows RT 8.1**
and **Windows 10 ARM32** — hardware the official Telegram apps abandoned. It talks MTProto
directly (via WTelegramClient) with no native Telegram dependency, so a single AnyCPU build
runs everywhere from a Surface RT to a modern x64 desktop.

## Runs on

- **Windows RT 8.1 (ARM32)** — the original Surface / Surface 2 (requires the RT jailbreak to run desktop apps)
- **Windows 10 ARM32**
- **Windows 8.1 / 10 / 11 (x86 / x64 /ARM64)**
- **Windows 7 (not tested yet)*

One `AnyCPU + Prefer32Bit` binary covers all of the above (32-bit process on every arch).

## Features

- Phone + 2FA login with silent session resume; QR login; multiple accounts
- Chat list and conversations with live updates, bidirectional paging, and combined search (chats + global messages)
- RTL-aware message bubbles (Persian / Arabic), emoji, and animated stickers (.tgs via rlottie)
- Photos with thumbnails + disk cache and a zoom/pan viewer; inline video / GIF (bundled VLC) with a system-player fallback
- Documents: file cards, open, Save / Save As, with background downloads (pause / resume / manager)
- Send media: attach or drag-and-drop photos, videos, and files — Compress / Send-as-file, caption, per-file progress, correct dimensions + poster thumbnails
- Record and send voice notes (OGG/Opus — pure-managed, ARM32-safe) and round video notes
- Message actions: reply (quoted), forward, copy, multi-select, delete (for me / for everyone), retry
- Pinned messages, folders, reactions, polls, inline keyboards & bots, group/channel admin
- Follow-system / Light / Dark theming that tracks the **Windows accent color live** (no restart)
- Configurable media auto-download and a managed cache folder; notifications with mute
- Touch panning, momentum scrolling, and the on-screen keyboard handled for tablet use

## Building

Requirements: **Visual Studio 2017**, **.NET Framework 4.7**, C# **7.3**.

1. Clone the repo.
2. **Provide your own Telegram API credentials** — they are deliberately kept out of source control:
   - Register an application at **https://my.telegram.org/apps** to get an `api_id` and `api_hash`.
   - In `TelegArm/Core/`, copy **`ApiCredentials.Local.cs.example`** → **`ApiCredentials.Local.cs`**
     and fill in your values. This file is gitignored and must never be committed.
   - Without it the project still builds (placeholders in `ApiCredentials.cs`), but it will **not authenticate**.
3. Build the solution (Debug or Release, `AnyCPU`). NuGet restores the package dependencies.

Everything the app needs at runtime — the bundled libVLC (per-arch), rlottie, Noto Emoji, and
fonts — is carried in the project and copied next to the executable on build, so no manual
codec/DLL setup is required.

## Continuing development (docs)

The repo is self-documenting so a new contributor — or a fresh AI session — can pick it up cold:

- **`CLAUDE.md`** — the top-level orientation (auto-loaded by Claude Code); start here.
- **`BUILD.md`** — exact build / run / installer commands and prerequisites.
- **`STATE.md`** — current status and the prioritized open-work list.
- **`INVARIANTS.md`** — the hard "don't regress these" rules (toolchain, account-data safety, UI/touch).
- **`mdfiles/`** — the deep docs: `HANDOFF.md` (full orientation), `CURRENT_STATE.md`, the dated
  `SESSION_*.md` narratives, `ARCHITECTURE_AND_LOGIC.md`, `LESSONS_LEARNED.md`, `BUGS_AND_FIXES.md`,
  `CREATIVE_DECISIONS.md`, `ROADMAP.md`, `THEME_UI_HANDOFF.md`.
- **`installer/INSTALLER_HANDOFF.md`** — how to build the RT-runnable AnyCPU installer.

## License

TelegArm is released under the **GNU General Public License v3.0 only (GPL-3.0-only)** — see
[LICENSE](LICENSE).

Bundled third-party components (WTelegramClient, MaterialSkin.2, LibVLCSharp / libVLC,
Newtonsoft.Json, NAudio, Concentus, SixLabors.ImageSharp, QRCoder, rlottie, and the Roboto /
Vazirmatn / Noto Emoji assets) remain under their own licenses — see
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Credits

Built on [WTelegramClient](https://github.com/wiz0u/WTelegramClient) by Wizou. Developed with
assistance from Claude (Anthropic).
