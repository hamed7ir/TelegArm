# =============================================================================
#  Builds the AnyCPU .NET installer (Setup.exe + payload.zip) from bin\Release.
#
#  WHY: an Inno Setup installer is native x86 and CANNOT run on Windows RT (ARM).
#  This installer is compiled AnyCPU/MSIL (like the app), so it runs on RT AND
#  x86/x64. Run this AFTER building Release (with ApiCredentials.Local.cs present).
#
#  Produces:
#    installer\Output\anycpu\Setup.exe        (AnyCPU installer)
#    installer\Output\anycpu\payload.zip      (the app in its dll\ deploy layout)
#    installer\Output\TelegArm-<version>-Setup-AnyCPU.zip   (ship this: extract, run Setup.exe)
# =============================================================================
$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root (...\TelegArm)
$rel    = Join-Path $root 'TelegArm\bin\Release'
$icon   = Join-Path $root 'img-icon\icon.ico'
$here   = $PSScriptRoot
$outdir = Join-Path $root 'installer\Output\anycpu'
if (-not (Test-Path "$rel\TelegArm.exe")) { throw "Build Release first: '$rel\TelegArm.exe' not found." }

# Version tracks the BUILT assembly (AssemblyInfo) so the distributable name never goes stale. Note: the
# AnyCPU wizard's DISPLAYED version is the AppVersion const in Setup.cs — keep it in sync with AssemblyInfo.
$ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo("$rel\TelegArm.exe").FileVersion   # e.g. 1.0.0.0
$ver = (($ver -split '\.')[0..2]) -join '.'                                                     # -> 1.0.0

# Locate a C# compiler (Roslyn preferred so Setup.cs can use C# 7.3; framework csc is the fallback).
$csc = 'D:\Program Files\Vscom\MSBuild\15.0\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }

# 1. Stage the payload in the dll\ deploy layout (native/data beside the exe; managed DLLs in dll\).
$stage = Join-Path $env:TEMP ("tgpay_" + [Guid]::NewGuid().ToString('N'))
New-Item $stage, "$stage\dll" -ItemType Directory -Force | Out-Null
Copy-Item "$rel\TelegArm.exe", "$rel\TelegArm.exe.config" $stage
Copy-Item "$rel\*.zip", "$rel\*.png", "$rel\*.ico" $stage
# Licence + notices (BATCH-TA-2/L1). GPL-3.0 s4 / LGPL-2.1 s6 / MIT / BSD / Apache / OFL all require
# the licence to ACCOMPANY the binary; until v1.1.0 neither installer shipped one. Setup.cs extracts
# payload.zip wholesale into the target directory, so staging them here puts them beside the exe on
# the device. This is the RT ship vehicle - it is the ONLY installer Windows RT can run.
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $stage
Copy-Item "$rel\rlottie" $stage -Recurse
Copy-Item "$rel\*.dll" "$stage\dll"

New-Item $outdir -ItemType Directory -Force | Out-Null
$payload = Join-Path $outdir 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload)
Remove-Item $stage -Recurse -Force

# 2. Compile Setup.exe as AnyCPU (MSIL -> runs on RT + x86/x64).
$setup = Join-Path $outdir 'Setup.exe'
# BATCH-TA-34/C1 — COMPILE Setup.exe AGAINST .NET 4.5, NOT 4.7.
# ⚠ THIS IS WHAT MAKES THE PREREQUISITE CHECK POSSIBLE AT ALL. An installer built against the very
#   runtime it is checking for cannot start on a machine that lacks it, so the user gets the same
#   cryptic Windows dialog the check exists to replace. Building against a LOWER surface means
#   Setup.exe runs, detects the missing 4.7, and explains — no native bootstrapper needed.
# ⚠ 4.5 IS THE FLOOR, NOT 4.0: Setup.cs uses ZipArchive (System.IO.Compression), which is 4.5+.
#   Dropping to 4.0 would mean hand-rolling zip extraction, which is not worth it — Windows 8 and
#   Windows RT 8.1 both ship 4.5 in-box, so the RT ship vehicle is unaffected. Residual gap: a
#   Windows 7 machine with only 4.0 still cannot run Setup.exe. Stated, not hidden.
# ⚠ /nostdlib+ + explicit reference-assembly paths is what actually retargets it. Without them csc
#   binds to the compiler's own (4.7+) assemblies and the /target switch is cosmetic.
# ⚠ NOT "${env:ProgramFiles(x86)}\..." — the parentheses inside ${} break the parser when it is
#   interpolated into a string. Fetch the variable explicitly and join.
$pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
if (-not $pf86) { $pf86 = $env:ProgramFiles }
$refRoot = Join-Path $pf86 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5'
if (-not (Test-Path "$refRoot\mscorlib.dll")) { throw "4.5 reference assemblies not found at $refRoot — cannot retarget Setup.exe" }
$refs = @('mscorlib','System','System.Core','System.Windows.Forms','System.Drawing',
          'System.IO.Compression','System.IO.Compression.FileSystem') |
        ForEach-Object { "/reference:$refRoot\$_.dll" }

& $csc /nologo /target:winexe /platform:anycpu /nostdlib+ /langversion:5 `
    "/out:$setup" "/win32icon:$icon" "/resource:$icon,TelegArmSetup.icon.ico" `
    $refs "$here\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host "  Setup.exe compiled against .NET 4.5 reference assemblies (prerequisite check can run)" -ForegroundColor DarkGray

# 3. Package the RT-runnable distributable (Setup.exe + payload.zip must travel together).
$dist = Join-Path $root "installer\Output\TelegArm-$ver-Setup-AnyCPU.zip"
if (Test-Path $dist) { Remove-Item $dist -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($outdir, $dist)

"Setup.exe arch = " + [System.Reflection.AssemblyName]::GetAssemblyName($setup).ProcessorArchitecture
"Distributable  = $dist  ({0} MB)" -f [int]((Get-Item $dist).Length / 1MB)
