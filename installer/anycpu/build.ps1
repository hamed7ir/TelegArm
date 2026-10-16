# =============================================================================
#  Builds TelegArm's release files from bin\Release. Redesign step 0.5: DroidFinestra's scripts\build.ps1
#  (@ 3708786), adapted. Run AFTER building Release (with ApiCredentials.Local.cs present):
#
#    .\installer\anycpu\build.ps1
#
#  Produces in installer\Output\ - written only after every check below has passed, so a failed run never
#  destroys the previous good output:
#    TelegArm-<ver>-Setup.exe       the installer: ONE file, the app embedded in it (AnyCPU, .NET 4.0 Client)
#    TelegArm-<ver>-Portable.zip    extract and run
#    SHA256SUMS.txt                 checksums of both, and the commit they were built from
#  <ver> = the built exe's AssemblyInformationalVersion (e.g. 2.0.0-alpha.1), else its file version.
#
#  WHY .NET 4.0 CLIENT FOR THE INSTALLER: it must START on a PC that lacks the 4.7 the app needs, to say so -
#  an installer built against the runtime it checks for cannot run to deliver the message. 4.0 Client is in
#  every Windows from RT 8.1 up. It has no ZipArchive, so the payload is embedded as one gzip resource per file.
#  /nostdlib+ with explicit reference assemblies is what actually retargets it: without them csc binds to its
#  own (4.7+) assemblies and the target is cosmetic.
#
#  ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
# =============================================================================
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root
$rel  = Join-Path $root 'TelegArm\bin\Release'
$exe  = Join-Path $rel 'TelegArm.exe'
$icon = Join-Path $root 'img-icon\icon.ico'
$here = $PSScriptRoot
$out  = Join-Path $root 'installer\Output'
if (-not (Test-Path -LiteralPath $exe)) { throw "Build Release first: $exe not found." }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function Sha256([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# A text file a user opens on the device: CRLF line ends, UTF-8 with BOM.
function WriteText([string]$path, [string]$text) {
    $crlf = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($path, $crlf, (New-Object Text.UTF8Encoding($true)))
}

# $true = a PE image with no CLR header (native code), $false = managed, $null = not a PE file.
function IsNativePe([string]$p) {
    $b = [IO.File]::ReadAllBytes($p)
    if ($b.Length -lt 64 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return $null }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ($pe -lt 0 -or $pe + 24 -gt $b.Length -or $b[$pe] -ne 0x50 -or $b[$pe + 1] -ne 0x45) { return $null }
    $opt = $pe + 24
    $plus = [BitConverter]::ToUInt16($b, $opt) -eq 0x20b
    $dirs = if ($plus) { $opt + 112 } else { $opt + 96 }
    $numDirs = [BitConverter]::ToInt32($b, $dirs - 4)
    if ($numDirs -lt 15) { return $true }
    return ([BitConverter]::ToUInt32($b, $dirs + 14 * 8) -eq 0)   # data directory 14 = the CLR header
}

# ---- 1. the built app ------------------------------------------------------------------------------------
# Loaded from BYTES: ReflectionOnlyLoadFrom would lock TelegArm.exe for the life of this PowerShell session,
# and the next MSBuild would then fail to overwrite it.
$appAsm = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($exe))
$attrs = $appAsm.GetCustomAttributesData()
$arch = [Reflection.AssemblyName]::GetAssemblyName($exe).ProcessorArchitecture
$tfm = ($attrs | Where-Object { $_.AttributeType.Name -eq 'TargetFrameworkAttribute' }).ConstructorArguments[0].Value
if ("$arch" -ne 'MSIL') { throw "TelegArm.exe is $arch, expected MSIL (AnyCPU)" }
if ($tfm -ne '.NETFramework,Version=v4.7') { throw "TelegArm.exe targets $tfm, expected .NET Framework 4.7 (rail R1)" }
$fileVer = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion            # 2.0.0.0
$infoAttr = $attrs | Where-Object { $_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute' }
$ver = if ($infoAttr) { [string]$infoAttr.ConstructorArguments[0].Value } else { (($fileVer -split '\.')[0..2]) -join '.' }
if ($ver -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "version '$ver' is not usable in a file name" }
"TelegArm.exe $ver  ($fileVer, $arch, $tfm)"

# Setup.cs repeats three identifiers of the app by hand. Prove the built app still contains each one: a
# drifted AUMID or CLSID makes Action Center silently stop working; a drifted mutex name makes the
# installer blind to a running TelegArm.
$src = [IO.File]::ReadAllText((Join-Path $here 'Setup.cs'))
$ids = @(
    [regex]::Match($src, 'Aumid = "([^"]+)"').Groups[1].Value,
    [regex]::Match($src, 'ToastActivatorClsid = "([^"]+)"').Groups[1].Value,
    [regex]::Match($src, 'MutexName = @"([^"]+)"').Groups[1].Value)
$ab = [IO.File]::ReadAllBytes($exe)
$u0 = [Text.Encoding]::Unicode.GetString($ab); $u1 = [Text.Encoding]::Unicode.GetString($ab, 1, $ab.Length - 1)   # both alignments
for ($i = 0; $i -lt $ids.Count; $i++) {
    $id = $ids[$i]
    if (-not $id) { throw 'could not read an identifier (Aumid / ToastActivatorClsid / MutexName) from Setup.cs' }
    # A GUID's case does not matter; a kernel object name's does - the mutex is compared exactly.
    $cmp = if ($i -eq 2) { [StringComparison]::Ordinal } else { [StringComparison]::OrdinalIgnoreCase }
    if ($u0.IndexOf($id, $cmp) -lt 0 -and $u1.IndexOf($id, $cmp) -lt 0) {
        throw "Setup.cs and TelegArm.exe disagree: '$id' is not in the built app" }
}
"identifiers shared with the app: " + ($ids -join ', ')

# ---- 2. stage the package (managed DLLs in dll\, probed via TelegArm.exe.config) ---------------------
$work = Join-Path ([IO.Path]::GetTempPath()) ('telegarm-build-' + [guid]::NewGuid().ToString('N'))
$files = Join-Path $work 'files'
New-Item -ItemType Directory -Force (Join-Path $files 'dll') | Out-Null
try {
    Copy-Item (Join-Path $rel 'TelegArm.exe'), (Join-Path $rel 'TelegArm.exe.config') $files
    # 2026-10-16: no *.zip any more: the libVLC zips are gone (FFmpeg below), and a stale one left in bin\Release by an
    # older build must not ship.
    Copy-Item (Join-Path $rel '*.png'), (Join-Path $rel '*.ico') $files
    # Licence + notices must ACCOMPANY the binary (GPL-3.0 s4, LGPL-2.1 s6, MIT/BSD/Apache/OFL).
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $files 'LICENSE.txt')
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $files
    Copy-Item (Join-Path $rel 'rlottie') $files -Recurse
    Copy-Item (Join-Path $rel 'ffmpeg') $files -Recurse   # FFmpeg + tgav per arch (ffmpeg\ARM, ffmpeg\x86): Core\Media\Tgav
    Copy-Item (Join-Path $rel 'tgcalls') $files -Recurse  # Telegram calls (tgcalls_native.dll per arch): Core\Calls\TgCalls
    Copy-Item (Join-Path $rel '*.dll') (Join-Path $files 'dll')
    # Text files as CRLF + UTF-8 BOM: Windows 8.1 Notepad shows an LF-only file as ONE line and may misread
    # BOM-less UTF-8. The working tree is CRLF only when git's autocrlf happens to be on (review).
    foreach ($t in Get-ChildItem -LiteralPath $files -Filter *.txt) { WriteText $t.FullName ([IO.File]::ReadAllText($t.FullName)) }

    # ---- 3. payload: one gzip resource per file + payload.list (sha256 TAB size TAB path [TAB N]) ---------
    $setupDir = Join-Path $work 'setup'
    $list = New-Object Collections.Generic.List[string]
    $res = New-Object Collections.Generic.List[string]
    $natives = 0
    foreach ($f in Get-ChildItem -LiteralPath $files -File -Recurse | Sort-Object FullName) {
        $relp = $f.FullName.Substring($files.Length + 1)
        if ($relp -match '[\s,"]') { throw "payload path '$relp' contains a space, comma or quote (resource names cannot)" }
        $gz = Join-Path (Join-Path $setupDir 'payload') ($relp + '.gz')
        New-Item -ItemType Directory -Force (Split-Path $gz) | Out-Null
        $in = [IO.File]::OpenRead($f.FullName); $os = [IO.File]::Create($gz)
        $z = New-Object IO.Compression.GZipStream($os, [IO.Compression.CompressionLevel]::Optimal)
        $in.CopyTo($z); $z.Dispose(); $os.Dispose(); $in.Dispose()
        $line = (Sha256 $f.FullName) + "`t" + $f.Length + "`t" + $relp
        if ((IsNativePe $f.FullName) -eq $true) { $line += "`tN"; $natives++ }
        $list.Add($line)
        $res.Add('/resource:"' + $gz + '",payload/' + $relp.Replace('\', '/') + '.gz')
    }
    $payloadList = Join-Path $setupDir 'payload.list'
    [IO.File]::WriteAllLines($payloadList, $list, (New-Object Text.UTF8Encoding($false)))
    "payload: $($list.Count) files, $natives native"

    # ---- 4. compile: the payload-free uninstaller first, then Setup with it embedded ----------------------
    $numeric = (($fileVer -split '\.')[0..3]) -join '.'
    $info = @"
using System.Reflection;
[assembly: AssemblyTitle("TelegArm Setup")]
[assembly: AssemblyProduct("TelegArm")]
[assembly: AssemblyCompany("Hamed (hamed7ir)")]
[assembly: AssemblyCopyright("GPL-3.0-only")]
[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyFileVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$ver")]
namespace TelegArmSetup
{
    internal static class Info
    {
        internal const string AppVersion = "$ver";
    }
}
"@
    $infoCs = Join-Path $setupDir 'SetupInfo.cs'
    [IO.File]::WriteAllText($infoCs, $info, (New-Object Text.UTF8Encoding($false)))

    $csc = $env:TELEGARM_CSC
    if (-not $csc) { $csc = 'D:\Program Files\Vscom\MSBuild\15.0\Bin\Roslyn\csc.exe' }
    if (-not (Test-Path -LiteralPath $csc)) { throw "csc not found: $csc (set TELEGARM_CSC to a Roslyn csc.exe)" }
    # NOT "${env:ProgramFiles(x86)}" inside a string: the parentheses break the parser there.
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)'); if (-not $pf86) { $pf86 = $env:ProgramFiles }
    $refs = Join-Path $pf86 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0\Profile\Client'
    if (-not (Test-Path -LiteralPath (Join-Path $refs 'mscorlib.dll'))) { throw ".NET 4.0 Client Profile reference assemblies not found: $refs" }
    $common = @('/nostdlib+', '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/langversion:5', '/codepage:65001',
                ('/win32icon:"' + $icon + '"'), ('/resource:"' + $icon + '",setup.icon.ico'))
    foreach ($r in 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms') { $common += '/r:"' + (Join-Path $refs "$r.dll") + '"' }
    $sources = @(('"' + (Join-Path $here 'Setup.cs') + '"'), ('"' + $infoCs + '"'))

    function Compile([string]$outExe, [string[]]$extra) {
        $rsp = Join-Path $setupDir ([IO.Path]::GetFileNameWithoutExtension($outExe) + '.rsp')
        $lines = @($common) + @('/out:"' + $outExe + '"') + $extra + $sources
        [IO.File]::WriteAllLines($rsp, [string[]]$lines, (New-Object Text.UTF8Encoding($false)))
        # /noconfig only counts on the command line (csc ignores it inside a response file).
        & $csc /noconfig "@$rsp"
        if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE) building $outExe" }
    }
    $uninst = Join-Path $setupDir 'uninstall.exe'
    Compile $uninst @()
    $setupName = "TelegArm-$ver-Setup.exe"
    $setupExe = Join-Path $setupDir $setupName
    Compile $setupExe (@($res) + @(('/resource:"' + $payloadList + '",payload.list'), ('/resource:"' + $uninst + '",uninstall.exe')))

    # ---- 5. check what came out ---------------------------------------------------------------------------
    foreach ($p in $uninst, $setupExe) {
        $a = [Reflection.AssemblyName]::GetAssemblyName($p).ProcessorArchitecture
        if ("$a" -ne 'MSIL') { throw "$p is $a, expected MSIL" }
    }
    $sAsm = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($setupExe))
    $srefs = ($sAsm.GetReferencedAssemblies() | ForEach-Object { $_.Name + ' ' + $_.Version }) -join ', '
    if ($srefs -match ' [1-9][0-9]*\.[1-9]' -or $srefs -notmatch 'mscorlib 4\.0\.0\.0') { throw "Setup must reference only 4.0.0.0 assemblies: $srefs" }
    $resNames = $sAsm.GetManifestResourceNames()
    if ($resNames.Count -ne $list.Count + 3) { throw "Setup carries $($resNames.Count) resources, expected $($list.Count + 3)" }
    $uAsm = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($uninst))
    if ($uAsm.GetManifestResourceNames() -contains 'payload.list') { throw 'uninstall.exe must not carry the payload' }
    "Setup: MSIL, refs = $srefs"
    "Setup: {0:N1} MB, uninstall.exe: {1:N0} KB" -f ((Get-Item $setupExe).Length / 1MB), ((Get-Item $uninst).Length / 1KB)

    # ---- 6. portable zip: the same files + PORTABLE-README.txt with the version stamped in -----------------
    $port = Join-Path $work 'portable'
    Copy-Item $files $port -Recurse
    $readme = [IO.File]::ReadAllText((Join-Path $root 'installer\PORTABLE-README.txt'), [Text.Encoding]::UTF8)
    if ($readme.IndexOf('{VERSION}') -lt 0) { throw 'PORTABLE-README.txt lost its {VERSION} placeholder' }
    WriteText (Join-Path $port 'PORTABLE-README.txt') $readme.Replace('{VERSION}', $ver)
    $portName = "TelegArm-$ver-Portable.zip"
    $portZip = Join-Path $work $portName
    # Entries written one by one with FORWARD slashes: ZipFile.CreateFromDirectory under Windows PowerShell 5.1
    # writes "dll\Foo.dll" (review; measured: 123 of 135 entries), which other unzip tools turn into a file
    # literally named "dll\Foo.dll".
    $zs = [IO.File]::Create($portZip)
    $za = New-Object IO.Compression.ZipArchive($zs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem -LiteralPath $port -File -Recurse | Sort-Object FullName) {
            $entry = $f.FullName.Substring($port.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $za.Dispose(); $zs.Dispose() }

    # ---- 7. every check passed: publish -----------------------------------------------------------------
    New-Item -ItemType Directory -Force $out | Out-Null
    Copy-Item $setupExe $out -Force
    Copy-Item $portZip $out -Force
    $commit = 'unknown'
    try {
        $commit = (& git -C $root rev-parse --short HEAD 2>$null)
        if (& git -C $root status --porcelain 2>$null) { $commit += ' (WITH UNCOMMITTED CHANGES - not a release build)' } else { $commit += ' (clean tree)' }
    } catch { }
    $sums = @("TelegArm $ver - SHA256 checksums", "built from commit $commit", '')
    foreach ($n in $setupName, $portName) { $sums += (Sha256 (Join-Path $out $n)) + '  ' + $n }
    $sums += @('', 'Verify on Windows:', "    certutil -hashfile $setupName SHA256", '', 'Verify with PowerShell:', "    Get-FileHash .\$setupName -Algorithm SHA256")
    [IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), [string[]]$sums, (New-Object Text.UTF8Encoding($false)))
    "setup:    " + (Join-Path $out $setupName)
    "portable: " + (Join-Path $out $portName)
    "sums:     " + (Join-Path $out 'SHA256SUMS.txt') + "  (commit $commit)"
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
