# TelegArm — Building the Release Installer (Handoff)

How the v1.0.0 release installers were produced, and how to rebuild them for the next
version. Everything here runs on the dev box (x64 Windows); the outputs land in
`installer/Output/`.

> ## ⚠ THE INNO (`.iss`) INSTALLER WAS DROPPED — 2026-08-06, BATCH-TA-33
> `installer/TelegArm.iss` is **deleted**. We ship exactly two things now:
> **the AnyCPU/MSIL installer** and **one portable package**.
> Every Inno / ISCC / `.iss` instruction below is therefore **obsolete** — the sections are left in
> place only because this file has not had a full rewrite yet. **Ignore §"Inno desktop installer",
> the ISCC prerequisite, and the `.iss` version-bump step.** Rebuilding this doc properly belongs to
> the packaging batch that is also adding the .NET 4.7 prerequisite check.
>
> **⚠ THAT BATCH MUST NOT DROP THE AppUserModelID** now stamped on the Start-Menu shortcut by
> `installer/anycpu/Setup.cs` (`Shortcut.Aumid` = `hamed7ir.TelegArm`, set via `IPropertyStore`
> **before** `IPersistFile.Save`). It must stay equal to `TelegArm.Helpers.ShellNotify.Aumid`. Without
> it, Action Center entries and the Start tile silently stop working — the app's own calls still
> succeed and simply deliver nothing. `Setup.exe` is itself managed .NET 4.7, so a real prerequisite
> check probably means a native bootstrapper, which would delete the whole `Shortcut` class and take
> the AUMID with it.
>
> **The portable package deliberately registers no shortcut**, so it has no AUMID and correctly gets
> no Action Center entry and no tile. The app logs that reason once at startup (`[SHELL] Action
> Center OFF — no Start-Menu shortcut carries AUMID …`) rather than looking broken.

## What we ship

| Package | File | Target | Notes |
|---|---|---|---|
| **AnyCPU .NET** (MSIL) | `TelegArm-<ver>-Setup-AnyCPU.zip` | **Windows RT (ARM)** + x86/x64 | Compiled AnyCPU/MSIL like the app, so it runs on RT. Per-user install to `%LocalAppData%\Programs\TelegArm` (no admin). **Registers the Start-Menu shortcut + AUMID.** |
| **Portable** | zip of `bin\Release` | anywhere | No install, no shortcut, no AUMID → notification window works, Action Center/tile do not. |

## Prerequisites (all present on the dev box)

- **VS2017 MSBuild** — `D:\Program Files\Vscom\MSBuild\15.0\Bin\msbuild.exe` (project is .NET Framework 4.7, AnyCPU + Prefer32Bit).
- **Real API credentials** — `TelegArm/Core/ApiCredentials.Local.cs` must exist (gitignored; the real `api_id`/`api_hash` supplied via the `FillId`/`FillHash` partial-method hooks). **Without it the build silently uses the placeholder and the shipped app won't authenticate.** To create it: copy `ApiCredentials.Local.cs.example` → `ApiCredentials.Local.cs` and fill in values from https://my.telegram.org/apps.
- **Inno Setup 6** — `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` (for the desktop installer).
- **Roslyn csc** — `D:\Program Files\Vscom\MSBuild\15.0\Bin\Roslyn\csc.exe` (compiles the AnyCPU `Setup.exe`; the script falls back to framework csc).
- **`icon.ico`** in `img-icon/` at the repo root (moved there with the other icon/image assets — ASSETS-TIDY).

## Bump the version BEFORE building

AssemblyInfo is the app's single source of truth (About screen / login title / the `app_version` sent to Telegram, all via `Program.Version`). The two installers carry their own version fields — keep them in sync:

1. `TelegArm/Properties/AssemblyInfo.cs` → `AssemblyVersion` **and** `AssemblyFileVersion` (`X.Y.Z.0`). Drives the app + the AnyCPU **zip filename** (build.ps1 reads it from the built exe).
2. `installer/TelegArm.iss` → `#define AppVersion "X.Y.Z"` (the Inno installer's version, propagates to its name/labels/uninstall entry).
3. `installer/anycpu/Setup.cs` → `internal const string AppVersion` (the AnyCPU wizard's displayed version + its uninstall registry `DisplayVersion`).

*(For a clean bump, `Program.cs`'s `ReadVersion()` fallback strings are cosmetic and only used if the assembly version can't be read.)*

## Build steps (exactly what was run for 1.0.0)

### 0. Close the app, then Rebuild Release
```powershell
Get-Process TelegArm -ErrorAction SilentlyContinue | Stop-Process -Force
& "D:\Program Files\Vscom\MSBuild\15.0\Bin\msbuild.exe" TelegArm.sln /t:Rebuild /p:Configuration=Release /v:minimal /nologo
```
→ `TelegArm/bin/Release/TelegArm.exe`. **Confirm** `(Get-Item …\bin\Release\TelegArm.exe).VersionInfo.FileVersion` == `X.Y.Z.0`. Both installers package this `bin\Release` folder, so it must be the freshly-built Release (not a stale one).

### 1. Inno desktop installer
```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\TelegArm.iss
```
→ `installer/Output/TelegArm-Setup-X.Y.Z.exe` (~120 MB, lzma2/max). ISCC resolves the `.iss`'s relative paths (`SrcDir = ..\TelegArm\bin\Release`, `..\icon.ico`, `OutputDir=Output`) against the `installer\` folder.

> **Expected benign warning:** *"PrivilegesRequired is set to admin but per-user areas … are used by the script."* That's intentional — the installer runs as admin (Program Files) but its `[UninstallDelete]` cleans the app's per-user data (session/cache/logs) on uninstall. Compile still succeeds (exit 0).

### 2. AnyCPU (RT-runnable) installer
```powershell
& "installer\anycpu\build.ps1"
```
→ `installer/Output/TelegArm-X.Y.Z-Setup-AnyCPU.zip` (~123 MB), plus the loose `installer/Output/anycpu/Setup.exe` + `payload.zip`. **Confirm** the script prints `Setup.exe arch = MSIL`.

## Output (what to ship)

```
installer/Output/
  TelegArm-Setup-1.0.0.exe            <- desktop x86/x64 (double-click → wizard)
  TelegArm-1.0.0-Setup-AnyCPU.zip     <- RT + any: extract, run Setup.exe
  anycpu/Setup.exe, anycpu/payload.zip  (loose copies; also inside the .zip)
```
`installer/Output/` is gitignored — these are build artifacts, not committed.

**Installing:**
- Desktop: run `TelegArm-Setup-1.0.0.exe` → Program Files, Start-menu shortcut.
- RT / no-admin: extract `TelegArm-1.0.0-Setup-AnyCPU.zip`, run `Setup.exe` (UI wizard) — or `Setup.exe --silent [dir]` / `Setup.exe --uninstall <dir>`. Installs per-user to `%LocalAppData%\Programs\TelegArm`.

## How the AnyCPU installer works (`installer/anycpu/build.ps1`)

1. **Stage the payload** in the *`dll\` deploy layout*: `TelegArm.exe` + `.config` + `*.zip` + `*.png` + `*.ico` + `rlottie\` sit **beside the exe**; all managed `*.dll` go under **`dll\`** (found at runtime via App.config `<probing privatePath="dll">`). Natives (`rlottie`), data zips (`libvlc-arm32/x86.zip`, `noto-emoji.zip`), and the `.config` **must** stay beside the exe — they're located by the app's own BaseDirectory logic, not the CLR.
2. **Zip** the stage → `payload.zip`.
3. **Compile** `Setup.cs` with `csc /target:winexe /platform:anycpu` (MSIL → runs on RT + x86/x64), embedding `icon.ico`.
4. **Zip** `Setup.exe` + `payload.zip` together → the distributable (they must travel as a pair; Setup.exe extracts payload.zip next to itself).

`Setup.exe` itself: per-user install, IShellLink COM shortcuts (Start-menu + desktop), an HKCU Uninstall registry entry, and `uninstall.exe` (a self-copy) that removes shortcuts + registry + per-user data (preserving user-saved media) then self-deletes.

## Gotchas / notes

- **RT cannot run the Inno `.exe`** (native x86 SetupLdr, no ARM emulation) — this is the whole reason the AnyCPU installer exists.
- **Write locations:** the app stores session/settings/cache/logs under the **user profile** (LocalAppData-first → Documents fallback), not beside the exe, so a Program Files install works for a normal (non-admin) user.
- **The `dll\` layout is post-install only.** The dev `bin\Release` stays flat (its own directory is probed first), so it still runs in place for development.
- **Branding art** (`installer/wizard-*.bmp`) is generated from `icon.ico`; regenerate if the icon changes (or drop in hand-designed BMPs with the same names).
- **Licensing:** `LICENSE` (GPL-3.0) at the repo root includes the GPL §7 additional permission that lets TelegArm link NAudio (Ms-PL). No action needed at install time — just don't drop the `LICENSE`/`THIRD-PARTY-NOTICES.txt` from a source distribution.

---
*Built for v1.0.0 on 2026-07-07. Both installers verified: Release exe 1.0.0.0, Inno compile exit 0, AnyCPU Setup.exe arch = MSIL.*
