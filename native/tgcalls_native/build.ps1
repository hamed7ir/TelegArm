# tgcalls_native: builds TelegArm's C ABI over tgcalls for both architectures with rt2.1 (clang-cl + its fixed lld-link,
# as the tgcalls handoff requires) against the handoff's static-library packages, and stages the DLL into
# TelegArm\tgcalls\<ARM|x86>\ (what Core\Calls\TgCalls loads). Windows PowerShell 5.1.
#
#   powershell -ExecutionPolicy Bypass -File native\tgcalls_native\build.ps1 [-Arch ARM|x86] [-Full]
#
# Inputs (environment variables of the same name override):
#   RT21     rt2.1's bin (the packaged release: clang-cl sha256 d572c493..., lld-link 6e203038...)
#   VCTOOLS  MSVC 14.44.35207;  WINSDK  Windows Kits\10 (SDK 10.0.19041.0: headers and the last ARM32 import libraries)
#   TGCALLS  the extracted packages: <TGCALLS>\arm32\tgcalls-1c236c09-win-arm32 and <TGCALLS>\x86\tgcalls-1c236c09-win-x86
#            (TGCALLS-HANDOFF-20261016\packages\*.zip, sha256 7709987e... / 2263e6c6...)
param([ValidateSet('all','ARM','x86')][string]$Arch = 'all', [switch]$Full)
$ErrorActionPreference = 'Stop'
function Env-Or($name, $default) { $v = [Environment]::GetEnvironmentVariable($name); if ($v) { $v } else { $default } }
$RT21    = Env-Or 'RT21'    'D:\repo\llvm-rt\dist\rt2.1\stage\rt2.1-23.1.1-rt2.1-arm32-win\bin'
$VCTOOLS = Env-Or 'VCTOOLS' 'D:\Program Files\vs22buildtools\VC\Tools\MSVC\14.44.35207'
$WINSDK  = Env-Or 'WINSDK'  'D:\Windows Kits\10'
$SDKVER  = '10.0.19041.0'
$TGC     = Env-Or 'TGCALLS' 'D:\tgcalls\packages'
$Here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Repo    = Resolve-Path (Join-Path $Here '..\..')

$clang = Join-Path $RT21 'clang-cl.exe'
$lld = Join-Path $RT21 'lld-link.exe'
$ver = (& $clang --version | Select-Object -First 1)
if ($ver -notmatch '23\.1\.1-rt2\.1') { throw "not rt2.1: $ver" }
foreach ($pair in @(@($clang, 'd572c49356dd897ef499131d8914bf6581a8a9a62dcf928220b7db786a1dfed2'),
                    @($lld,   '6e203038eb1b3ac1015f36e3595b456623ca4592c66b998ab0b8ccf0c38afb30'))) {
    $h = (Get-FileHash $pair[0] -Algorithm SHA256).Hash.ToLower()
    if ($h -ne $pair[1]) { throw "$($pair[0]) hash $h is not the rt2.1 release" }
}
Write-Host "toolchain: $ver (clang-cl + lld-link hashes match rt2.1)"

# BUILD-INFO.txt: ABI-relevant defines for every TU that includes tgcalls / tg_owt headers
$defines = 'TGCALLS_USE_STD_OPTIONAL WEBRTC_APP_TDESKTOP RTC_ENABLE_H265 RTC_ENABLE_VP9 WEBRTC_ENABLE_PROTOBUF=0 ' +
           'WEBRTC_INCLUDE_INTERNAL_AUDIO_DEVICE HAVE_SCTP WEBRTC_USE_H264 WEBRTC_USE_BUILTIN_ISAC_FLOAT WEBRTC_LIBRARY_IMPL ' +
           'WEBRTC_NON_STATIC_TRACE_EVENT_HANDLERS=1 WEBRTC_HAVE_DCSCTP WEBRTC_HAVE_SCTP ABSL_ALLOCATOR_NOTHROW=1 WEBRTC_WIN ' +
           'RTC_ENABLE_WIN_WGC ZLIB_WINAPI WIN32 WIN32_LEAN_AND_MEAN _WINDOWS NOMINMAX UNICODE _UNICODE NDEBUG'
$libs = 'tgcalls.lib tg_owt.lib ssl.lib crypto.lib avformat.lib avcodec.lib swresample.lib swscale.lib avutil.lib ' +
        'opus.lib vpx.lib openh264.lib jpeg-static.lib rnnoise.lib zlibstatic.lib ' +
        'secur32.lib d3d11.lib dxgi.lib dwmapi.lib shcore.lib strmiids.lib winmm.lib ws2_32.lib iphlpapi.lib crypt32.lib ' +
        'bcrypt.lib ole32.lib oleaut32.lib uuid.lib user32.lib gdi32.lib advapi32.lib shell32.lib shlwapi.lib version.lib avrt.lib ' +
        'mfuuid.lib runtimeobject.lib'   # the 8.1 camera sink: Media Foundation GUIDs (static) and WinRT strings; mfplat.dll at run time

$targets = @(
    @{ Name = 'ARM'; Triple = 'armv7-pc-windows-msvc'; Machine = 'arm'; Pkg = (Join-Path $TGC 'arm32\tgcalls-1c236c09-win-arm32') },
    @{ Name = 'x86'; Triple = 'i686-pc-windows-msvc';  Machine = 'x86'; Pkg = (Join-Path $TGC 'x86\tgcalls-1c236c09-win-x86') }
)
foreach ($t in $targets) {
    if ($Arch -ne 'all' -and $t.Name -ne $Arch) { continue }
    $inc = Join-Path $t.Pkg 'include'
    $lib = Join-Path $t.Pkg 'lib'
    if (-not (Test-Path (Join-Path $lib 'tgcalls.lib'))) { throw "no tgcalls.lib in ${lib}: extract the handoff package" }
    $out = Join-Path $Here ("out\" + $t.Name)
    New-Item -ItemType Directory -Force $out | Out-Null
    $clArgs = @("--target=$($t.Triple)", '-vctoolsdir', $VCTOOLS, '-winsdkdir', $WINSDK, '-winsdkversion', $SDKVER,
                '-fuse-ld=lld', '/nologo', '/O2', '/MT', '/GR', '/EHsc', '/std:c++20', '/permissive-', '/Zc:__cplusplus',
                '/utf-8', '/bigobj', '/D_CRT_SECURE_NO_WARNINGS', '/W1')
    foreach ($d in $defines.Split(' ')) { $clArgs += "/D$d" }
    foreach ($i in @('tgcalls', 'tg_owt', 'tg_owt\third_party\abseil-cpp', 'tg_owt\third_party\libyuv\include')) { $clArgs += "/I$(Join-Path $inc $i)" }
    $clArgs += "/I$Here"
    # Incremental (each file costs minutes on the tgcalls headers): a source is compiled again only when it, one of our
    # headers or this script is newer than its object; -Full rebuilds all.
    $newest = @((Get-ChildItem $Here -Filter '*.h') + (Get-Item $MyInvocation.MyCommand.Path) | ForEach-Object { $_.LastWriteTimeUtc }) | Sort-Object | Select-Object -Last 1
    $objs = @()
    Write-Host "== $($t.Name): clang-cl $($t.Triple) + lld-link"
    foreach ($src in @('tgcalls_native.cpp', 'tgcalls_video.cpp', 'tgcalls_camsink.cpp', 'tgcalls_group.cpp')) {
        $obj = Join-Path $out ([IO.Path]::ChangeExtension($src, '.obj'))
        $objs += $obj
        $srcFile = Get-Item (Join-Path $Here $src)
        if (-not $Full -and (Test-Path $obj)) {
            $objTime = (Get-Item $obj).LastWriteTimeUtc
            if ($objTime -gt $srcFile.LastWriteTimeUtc -and $objTime -gt $newest) { Write-Host "   up to date: $src"; continue }
        }
        Write-Host "   compiling: $src"
        & $clang @clArgs '/c' $srcFile.FullName "/Fo$obj"
        if ($LASTEXITCODE -ne 0) { throw "tgcalls_native $($t.Name): $src failed ($LASTEXITCODE)" }
    }
    $linkArgs = @("--target=$($t.Triple)", '-vctoolsdir', $VCTOOLS, '-winsdkdir', $WINSDK, '-winsdkversion', $SDKVER, '-fuse-ld=lld', '/nologo', '/MT', '/LD') +
                $objs + @("/Fe$out\tgcalls_native.dll", '/link', "/machine:$($t.Machine)", "/libpath:$lib", '/OPT:REF', '/OPT:ICF') + $libs.Split(' ')
    & $clang @linkArgs
    if ($LASTEXITCODE -ne 0) { throw "tgcalls_native $($t.Name) link failed ($LASTEXITCODE)" }
    $stage = Join-Path $Repo ("TelegArm\tgcalls\" + $t.Name)
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item (Join-Path $out 'tgcalls_native.dll') $stage -Force
    Get-Item (Join-Path $stage 'tgcalls_native.dll') | ForEach-Object { "{0,-22} {1,12:N0}" -f $_.Name, $_.Length }
}
Write-Host "staged: TelegArm\tgcalls\<arch>\tgcalls_native.dll"
