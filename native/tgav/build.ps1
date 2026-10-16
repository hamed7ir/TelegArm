# tgav: builds TelegArm's FFmpeg shim for both architectures with rt2.1, and stages it with the ffmpeg-rt DLLs into
# TelegArm\ffmpeg\<ARM|x86>\ (what the app loads: Core\Media\Tgav.cs). Windows PowerShell 5.1; no MSYS2 needed.
#
#   powershell -ExecutionPolicy Bypass -File native\tgav\build.ps1
#
# Inputs (override with the environment variables of the same name):
#   RT21     rt2.1's bin (the packaged release; clang-cl sha256 d572c493..., llvm-rt dist\rt2.1\MANIFEST.txt)
#   VCTOOLS  MSVC 14.44.35207 (the last with ARM32 libraries)
#   WINSDK   Windows Kits\10, SDK 10.0.19041.0 (the last with ARM32 libraries)
#   FFRT     ffmpeg-rt's dist (dist\shared = ARM32, dist\shared-x86 = x86): headers, import libraries, DLLs
param([ValidateSet('all','ARM','x86')][string]$Arch = 'all')   # one architecture: e.g. x86 while ARM32 FFmpeg still builds
$ErrorActionPreference = 'Stop'
function Env-Or($name, $default) { $v = [Environment]::GetEnvironmentVariable($name); if ($v) { $v } else { $default } }
$RT21    = Env-Or 'RT21'    'D:\repo\llvm-rt\dist\rt2.1\stage\rt2.1-23.1.1-rt2.1-arm32-win\bin'
$VCTOOLS = Env-Or 'VCTOOLS' 'D:\Program Files\vs22buildtools\VC\Tools\MSVC\14.44.35207'
$WINSDK  = Env-Or 'WINSDK'  'D:\Windows Kits\10'
$SDKVER  = '10.0.19041.0'
$FFRT    = Env-Or 'FFRT'    'D:\repo\ffmpeg-rt\dist'
$Here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Repo    = Resolve-Path (Join-Path $Here '..\..')

# Toolchain gate (ffmpeg-rt CLAUDE.md): the version disqualifies, the hash identifies.
$clang = Join-Path $RT21 'clang-cl.exe'
$ver = (& $clang --version | Select-Object -First 1)
if ($ver -notmatch '23\.1\.1-rt2\.1') { throw "not rt2.1: $ver" }
$sha = (Get-FileHash $clang -Algorithm SHA256).Hash.ToLower()
if ($sha -ne 'd572c49356dd897ef499131d8914bf6581a8a9a62dcf928220b7db786a1dfed2') { throw "clang-cl hash $sha is not the rt2.1 release" }
Write-Host "toolchain: $ver ($sha)"

$targets = @(
    @{ Name = 'ARM'; Triple = 'armv7-pc-windows-msvc'; Lib = 'arm'; Machine = 'arm'; Ff = (Join-Path $FFRT 'shared') },
    @{ Name = 'x86'; Triple = 'i686-pc-windows-msvc';  Lib = 'x86'; Machine = 'x86'; Ff = (Join-Path $FFRT 'shared-x86') }
)
foreach ($t in $targets) {
    if ($Arch -ne 'all' -and $t.Name -ne $Arch) { continue }
    $out = Join-Path $Here ("out\" + $t.Name)
    New-Item -ItemType Directory -Force $out | Out-Null
    $inc = Join-Path $t.Ff 'include'
    $ffbin = Join-Path $t.Ff 'bin'
    if (-not (Test-Path (Join-Path $ffbin 'avfilter.lib'))) { throw "no avfilter.lib in ${ffbin}: build ffmpeg-rt first" }
    $clArgs = @(
        "--target=$($t.Triple)", '-vctoolsdir', $VCTOOLS, '-winsdkdir', $WINSDK, '-winsdkversion', $SDKVER,
        '/nologo', '/O2', '/MT', '/W3', '/LD', '/DNDEBUG', '/D_CRT_SECURE_NO_WARNINGS',
        "/I$inc", (Join-Path $Here 'tgav.c'), "/Fo$out\tgav.obj", "/Fe$out\tgav.dll",
        '/link', "/machine:$($t.Machine)", "/libpath:$ffbin",
        'avformat.lib', 'avcodec.lib', 'avutil.lib', 'swscale.lib', 'avfilter.lib'
    )
    Write-Host "== $($t.Name): clang-cl $($t.Triple)"
    & $clang @clArgs
    if ($LASTEXITCODE -ne 0) { throw "tgav $($t.Name) failed ($LASTEXITCODE)" }

    # Stage: tgav.dll + the FFmpeg DLLs it pulls in (avfilter brings swresample and swscale).
    $stage = Join-Path $Repo ("TelegArm\ffmpeg\" + $t.Name)
    New-Item -ItemType Directory -Force $stage | Out-Null
    Get-ChildItem $stage -Filter *.dll | Remove-Item -Force
    Copy-Item (Join-Path $out 'tgav.dll') $stage
    Get-ChildItem $ffbin -Filter *.dll | Copy-Item -Destination $stage
    Get-ChildItem $stage -Filter *.dll | ForEach-Object { "{0,-24} {1,10:N0}" -f $_.Name, $_.Length }
}
Write-Host "staged: TelegArm\ffmpeg\ARM, TelegArm\ffmpeg\x86"
