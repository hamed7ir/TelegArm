# TelegArm — Building the Release Packages (Handoff)

How the release packages are produced. Everything here runs on the dev box (x64 Windows); the
outputs land in `installer/Output/`.

## What we ship — two packages, nothing else

| Package | File | Target | Notes |
|---|---|---|---|
| **AnyCPU installer** | `TelegArm-<ver>-Setup-AnyCPU.zip` (contains `Setup.exe` + `payload.zip`) | **Windows RT (ARM32)** + x86/x64 | Compiled AnyCPU/MSIL like the app, so it runs on RT. Per-user install to `%LocalAppData%\Programs\TelegArm` (no admin). **Registers the Start-Menu shortcut carrying the AUMID.** |
| **Portable** | zip of `bin\Release` + `PORTABLE-README.txt` | anywhere | No install, no shortcut, **no AUMID** → the notification window works; Action Center and the Start tile do not, by design. |

> **The Inno Setup (`.iss`) installer was deleted in 2026-08 (BATCH-TA-33).** Its loader was native
> x86 and could never run on RT, which is why the AnyCPU installer existed alongside it; once the
> AnyCPU one covered desktop too, the second vehicle was pure maintenance. There is no `.iss`, no
> ISCC step and no wizard `.bmp` pipeline any more.

## Prerequisites (dev box)

- **VS2017 MSBuild** — `D:\Program Files\Vscom\MSBuild\15.0\Bin\msbuild.exe` (the app is .NET
  Framework 4.7, AnyCPU + Prefer32Bit).
- **Roslyn csc** — `D:\Program Files\Vscom\MSBuild\15.0\Bin\Roslyn\csc.exe` (compiles `Setup.exe`;
  the script falls back to framework csc).
- **.NET 4.5 reference assemblies** —
  `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\`.
  **The build throws if these are missing** — see "Why Setup.exe targets 4.5" below.
- **Real API credentials** — `TelegArm/Core/ApiCredentials.Local.cs` must exist (gitignored). Without
  it the build silently uses the placeholder and the shipped app **cannot authenticate**. Copy
  `ApiCredentials.Local.cs.example` and fill in values from https://my.telegram.org/apps.
- **`icon.ico`** in `img-icon/` at the repo root.

## Bump the version BEFORE building

`TelegArm/Properties/AssemblyInfo.cs` is the source of truth — `Program.Version` reads it,
`TelegramService` reports it to Telegram as `app_version`, and `build.ps1` derives the package name
from the built exe's `FileVersion`.

⚠ **There is one hardcoded copy: `installer/anycpu/Setup.cs` → `AppVersion`.** It exists because
`Setup.exe` is compiled separately and has no reference to the app. **Bump both**, or the Add/Remove
Programs entry disagrees with the binary it installed.

## Build

```powershell
# 0. Close the app first — the running exe locks it.
& "D:\Program Files\Vscom\MSBuild\15.0\Bin\msbuild.exe" TelegArm.sln /t:Rebuild /p:Configuration=Release

# 1. Installer (stages the payload, compiles Setup.exe, zips the distributable)
powershell -ExecutionPolicy Bypass -File installer\anycpu\build.ps1
```

Output: `installer/Output/TelegArm-<ver>-Setup-AnyCPU.zip`. **`Setup.exe` and `payload.zip` must
travel together** — Setup reads the payload from beside itself.

For the portable package, zip `TelegArm\bin\Release` together with `installer\PORTABLE-README.txt`.

## ★ Why `Setup.exe` targets .NET 4.5 (and why the flags matter)

The app needs **.NET 4.7**. Without it, it dies at startup with a Windows dialog that never says
"install .NET" — so the installer has to say it instead.

**An installer compiled against 4.7 cannot deliver that message**, because it would fail to start on
exactly the machines that need to hear it. So `Setup.exe` is built against a *lower* surface: it
runs, detects the missing 4.7, explains, and exits. That is what removes the need for a native
bootstrapper.

⚠ **`/nostdlib+` with explicit `/reference:` paths into the v4.5 folder is what actually retargets
it.** Without them, csc binds its own (4.7+) assemblies and the retarget is **cosmetic** — the build
appears to succeed and `Setup.exe` still won't start on a 4.5 box. This is the trap; the build script
throws if the reference assemblies are absent rather than quietly producing a 4.7 binary.

⚠ **4.5 is the floor, not 4.0**, because `Setup.cs` uses `ZipArchive` (`System.IO.Compression`,
4.5+). Dropping to 4.0 means hand-rolling zip extraction. Windows 8 and **RT 8.1 both ship 4.5
in-box**, so the RT vehicle is unaffected. Residual gap, stated rather than hidden: a Windows 7
machine with only 4.0 still cannot run `Setup.exe`.

## The 4.7 prerequisite check

`Setup.cs` reads `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` → `Release` and requires
**>= 460798**. If it is lower or absent, it explains and **exits with code 2** rather than installing
something that cannot start.

- It **fails open** on an unreadable registry — a permissions oddity must not block an install.
- It sits **after** the `--uninstall` branch on purpose: removing TelegArm must still work on a
  machine whose runtime was uninstalled first.
- ⚠ **The download pointer branches on architecture.** Microsoft's web installer **does not serve
  ARM32**; pointing an RT user at it hands them a file that cannot run, which is worse than no link.
  ARM32 gets the Open-RT mirror (`files.open-rt.party/Software/Redistributables/`).

## ★ The AUMID on the Start-Menu shortcut — and what silently breaks without it

`Setup.cs`'s `Shortcut.Create` stamps **`hamed7ir.TelegArm`** onto the `.lnk` by QI'ing the
`IShellLink` to `IPropertyStore` and setting `System.AppUserModel.ID`, **before**
`IPersistFile.Save` (the property store is written as part of saving the link).

**Windows identifies a desktop app to the notification system by finding a Start-Menu shortcut
carrying this string.** Without it:

- Action Center entries and the Start tile **silently do nothing** — the app's own calls still
  *succeed* (`CreateToastNotifier(aumid)` returns an object for any string at all), delivery just
  never happens. Only reading `ToastNotifier.Setting`, which throws `0x80070490`, reveals it.
- The notification **window** and the **taskbar badge** are unaffected — neither needs an AUMID.

⚠ **The string exists in exactly two places and must not drift:**
`TelegArm/Helpers/ShellNotify.cs` → `Aumid`, and `installer/anycpu/Setup.cs` → `Shortcut.Aumid`.

⚠ **Do not delete the `Shortcut` class.** If this installer is ever replaced (e.g. by a native
bootstrapper), the AUMID must be carried over, or notifications regress to window-only **with no
error anywhere**.

## How `build.ps1` works

1. **Stage** the payload in the post-install `dll\` layout — native/data beside the exe, managed DLLs
   in `dll\`.
2. **Licence + notices** (BATCH-TA-2/L1): copies `LICENSE` → `LICENSE.txt` and
   `THIRD-PARTY-NOTICES.txt` into the stage. GPL-3.0 §4 / LGPL-2.1 §6 / MIT / BSD / Apache / OFL all
   require the licence to **accompany** the binary. Setup extracts `payload.zip` wholesale into the
   target directory, so staging them here puts them beside the exe on the device.
   ⚠ **Do not drop this step.** Until v1.1.0 no installer shipped a licence at all.
3. **Zip** the stage into `payload.zip`.
4. **Compile** `Setup.exe` AnyCPU against the 4.5 reference assemblies (see above).
5. **Package** `Setup.exe` + `payload.zip` into the distributable zip.

## Gotchas

- **Write locations:** the app stores session/settings/cache/logs under the **user profile**
  (LocalAppData-first → Documents fallback), never beside the exe.
- **The `dll\` layout is post-install only.** The dev `bin\Release` stays flat (its own directory is
  probed first), so it still runs in place for development.
- **Licensing:** `LICENSE` (GPL-3.0) includes the GPL §7 additional permission that lets TelegArm
  link NAudio (Ms-PL). Don't drop `LICENSE` / `THIRD-PARTY-NOTICES.txt` from a source distribution
  either.
- **Portable cannot self-check the runtime.** It is 4.7-targeted, so on a machine without 4.7 it
  fails before its own code runs. `PORTABLE-README.txt` states the requirement with the same
  architecture-aware pointer.
