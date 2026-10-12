# TelegArm — Building the Release Packages (Handoff)

How the release packages are produced. Everything here runs on the dev box (x64 Windows); the
outputs land in `installer/Output/`. Since redesign step 0.5 (2.0.0) the installer is **one exe**,
ported from DroidFinestra's installer.

## What we ship — two packages, nothing else

| Package | File | Target | Notes |
|---|---|---|---|
| **Installer** | `TelegArm-<ver>-Setup.exe` — one file, the app embedded in it | **Windows RT 8.1 (ARM32)**, Windows 10 ARM32, Windows 7–11 x86/x64/ARM64 | AnyCPU/MSIL against **.NET 4.0 Client**, so it starts on RT. Per-user install to `%LocalAppData%\Programs\TelegArm` (no admin). **Registers the Start-Menu shortcut carrying the AUMID + toast-activator CLSID.** |
| **Portable** | `TelegArm-<ver>-Portable.zip` — the same files + `PORTABLE-README.txt` | anywhere | No install, no shortcut, **no AUMID** → the notification window works; Action Center does not, by design. |

`SHA256SUMS.txt` beside them records both checksums and the commit (it says so loudly when the tree
had uncommitted changes — such a build is not a release).

## Prerequisites (dev box)

- **VS2017 MSBuild** — `D:\Program Files\Vscom\MSBuild\15.0\Bin\msbuild.exe` (the app is .NET
  Framework 4.7, AnyCPU + Prefer32Bit).
- **Roslyn csc** — `D:\Program Files\Vscom\MSBuild\15.0\Bin\Roslyn\csc.exe`, or set `TELEGARM_CSC`.
- **.NET 4.0 Client Profile reference assemblies** —
  `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0\Profile\Client\`.
  **The build throws if these are missing** — see "Why Setup targets .NET 4.0 Client" below.
- **Real API credentials** — `TelegArm/Core/ApiCredentials.Local.cs` must exist (gitignored). Without
  it the build silently uses the placeholder and the shipped app **cannot authenticate**.
- **`icon.ico`** in `img-icon/` at the repo root.

## The version — one place

`TelegArm/Properties/AssemblyInfo.cs` is the **only** source: `Program.Version` reads
`AssemblyVersion` (Major.Minor.Build — what Telegram sees as `app_version`, so keep it numeric), and
`build.ps1` names the packages and stamps `Setup.exe` (title, Add/Remove Programs version) from the
built exe's **`AssemblyInformationalVersion`** — e.g. `2.0.0-alpha.1` for a device test build. Drop
the suffix to release. There is no second copy in `Setup.cs` any more (it used to have one).

## Build

```powershell
# 0. Close the app first — the running exe locks it.
& "D:\Program Files\Vscom\MSBuild\15.0\Bin\msbuild.exe" TelegArm.sln /t:Rebuild /p:Configuration=Release

# 1. Packages: Setup.exe, the portable zip, SHA256SUMS.txt. Run in a FRESH PowerShell (-File), as below.
powershell -ExecutionPolicy Bypass -File installer\anycpu\build.ps1
```

## How `build.ps1` works

1. **Checks the built app**: MSIL, targets .NET 4.7, a usable version. And it proves the three
   identifiers `Setup.cs` repeats by hand — the **AUMID**, the **toast-activator CLSID** and the
   **single-instance mutex name** — are really in the built `TelegArm.exe`. A drifted one fails the
   build instead of silently breaking Action Center or the running-app check.
2. **Stages** the package in the `dll\` layout (managed DLLs in `dll\`, probed via
   `TelegArm.exe.config`; native/data beside the exe), plus **`LICENSE.txt` and
   `THIRD-PARTY-NOTICES.txt`**. GPL-3.0 §4 / LGPL-2.1 §6 / MIT / BSD / Apache / OFL all require the
   licence to **accompany** the binary. ⚠ **Do not drop this step.**
3. **Payload**: every staged file → one gzip resource `payload/<path>.gz`; `payload.list` records
   `sha256 TAB size TAB path [TAB N]` (`N` = a native PE — the only files the requirements check parses).
4. **Compiles twice from the one `Setup.cs`**: a payload-free `uninstall.exe` (~0.8 MB), then
   `Setup.exe` with the payload **and that uninstaller** embedded — so an install folder does not carry a
   second copy of the 125 MB package.
5. **Verifies**: both MSIL, Setup references only 4.0.0.0 assemblies, the resource count matches,
   the uninstaller carries no payload.
6. **Portable zip** from the same staged files + `PORTABLE-README.txt` (its `{VERSION}` stamped).
7. **Publishes** to `installer/Output/` — only after every check passed, so a failed run leaves the
   previous good output alone.

## ★ Why Setup targets .NET 4.0 Client (and why the flags matter)

The app needs **.NET 4.7**; without it, it dies at startup with a Windows dialog that never says
"install .NET". An installer compiled against 4.7 could not run to say so. 4.0 Client is the lowest
surface every supported Windows has (RT 8.1 ships 4.5), so Setup always starts and can explain.

⚠ **`/nostdlib+` with explicit references into the v4.0 Client folder is what actually retargets it.**
Without them csc binds its own (4.7+) assemblies and the retarget is **cosmetic**. The build throws if
the reference assemblies are absent, and checks the result references only 4.0.0.0.

4.0 has no `ZipArchive` and no `async` — hence the per-file gzip resources and plain threads.

## The .NET check — informs, never blocks (owner's rule)

`Requirements.NetFull` reads `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full`. Below 4.7
(release 460798) or absent, **Install** shows a note with the right link and lets the user continue
(default Yes); `--silent` writes it to stderr and installs. **Check requirements** reports it.
- A 4.7-targeted app runs on an older 4.x unless it calls a 4.7-only API. TA-42: an older installer
  that *refused* on release 460788 blocked a Windows 10 ARM32 device where TelegArm ran perfectly.
- It fails open on an unreadable registry.
- The link branches on architecture: ARM → `https://go.microsoft.com/fwlink/?linkid=2088632` (what RT
  8.1 users install; Windows 10 ARM32 already has 4.7), else the .NET Framework download page.

## ★ The AUMID and CLSID on the Start-Menu shortcut — and what silently breaks without them

`Shortcut.Create` stamps **`System.AppUserModel.ID = hamed7ir.TelegArm`** and
**`System.AppUserModel.ToastActivatorCLSID`** onto the `.lnk` via `IPropertyStore`, **before**
`IPersistFile.Save`. The installer also registers that CLSID's `LocalServer32` (HKCU) → `TelegArm.exe`.
Windows identifies an unpackaged app to the notification system **by that shortcut**; without it,
Action Center entries **silently never arrive** (the app's calls still succeed). The notification
window and the taskbar badge are unaffected. ⚠ **Never drop the `Shortcut` class.**

## Install, upgrade, uninstall — the safety rules

- **A running TelegArm is never killed.** Install and uninstall check the app's machine-wide mutex
  (`Global\TelegArm_SingleInstance`) and for a `TelegArm.exe` running from the folder, then say
  "close it, then Retry" (`--silent`: exit 4). With "start with Windows" on, *running in the tray* is
  the normal state during an upgrade.
- **No recursive delete of an install folder, anywhere.** The install writes `uninstall.manifest`
  (every file it places) and `uninstall.exe` **first, before extracting**, so a failed install still
  leaves a folder Setup can retry into and Settings → Apps can remove. Upgrade and uninstall delete
  **only the listed files** (+ `libvlc\`, `.vlc_extract_*`, `.write_test_*`, which the app itself
  creates beside its exe — `VlcBootstrap` tries that folder first), then the folders that leaves empty;
  the folder itself goes with a **non-recursive** `rmdir` run from `%TEMP%`. A file the user put there
  survives. Without a manifest, an upgrade deletes only the names this package installs (a 1.x install,
  or a portable copy unzipped flat into a shared folder), and an uninstall deletes nothing and says so.
- **It refuses** to install into a non-empty folder that is not TelegArm's, to install into or
  uninstall a drive root or well-known folder, and to uninstall a folder without the manifest,
  `TelegArm.exe`, or the uninstall entry naming it (Setup renamed `uninstall.exe` in Downloads must
  not be able to empty Downloads).
- **Uninstall keeps the user's data by default.** Interactive uninstall asks — "Also remove your
  TelegArm data from this PC?" — **default No**. `--silent` keeps it unless `--purge`. Purge removes
  `%LocalAppData%\TelegArm`, `%AppData%\TelegArm` and the app's own files in `Documents\TelegArm`
  (settings and their backups, accounts, cache, logs, libvlc, theme), never files the user saved there.
- **Ownership:** the shortcuts, the uninstall entry, the activator class and the "start with Windows"
  Run value are removed **only if they point into the folder being removed** — another copy may own them.

### Command line

```
TelegArm-<ver>-Setup.exe                                  wizard
TelegArm-<ver>-Setup.exe --check [file]                   requirements report; exit 0 / 3 missing-or-old / 2 cannot run
TelegArm-<ver>-Setup.exe --silent [dir] [--no-desktop]    exit 0 / 4 TelegArm running / 1 error
uninstall.exe [--uninstall <dir>] [--silent] [--purge]    exit 0 / 4 TelegArm running / 1 not an install or error
```

## Verified (2026-10-12, dev box, build `2.0.0-alpha.1`)

A scratch-folder suite (60 checks, `mdfiles/probes/installer-test-0.5.ps1`, local): refuse a foreign non-empty folder; refuse install and uninstall while the
mutex is held; install = payload + uninstall.exe + manifest, `TelegArm.exe` byte-identical, shortcut
AUMID read back; upgrade keeps a user file and drops `libvlc\`; uninstalling copy A leaves copy B's
registrations; uninstall removes shortcuts, uninstall entry, activator and Run value; installing over a
loose portable copy keeps the user's files; a failed-install folder is recoverable; a trailing backslash
and `--silent` in any position work; the portable zip has forward-slash entries and CRLF+BOM text; a renamed
uninstaller in a random folder refuses; the profile data root untouched. **Not yet run on the RT
device** — the requirements report there (rlottie's ARM imports) is the first thing to read.

## Gotchas

- **Write locations:** the app keeps session/settings/cache/logs under the **user profile**
  (LocalAppData-first → Documents), never beside the exe — **except `libvlc\`**, extracted beside the
  exe when that folder is writable (it is, for a per-user install).
- **The `dll\` layout is packaging only.** `bin\Release` stays flat and runs in place.
- **Licensing:** `LICENSE` (GPL-3.0) includes the §7 permission that lets TelegArm link NAudio (Ms-PL).
- **Portable cannot self-check the runtime** — it is 4.7-targeted and fails before its own code runs;
  `PORTABLE-README.txt` states the requirement with the same links.
