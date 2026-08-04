# rlottie — how the bundled DLLs were built

TelegArm ships **prebuilt** `rlottie.dll` for two architectures. They are **our own builds**, not
upstream release binaries (upstream publishes no Windows-ARM32 build), so this file records how to
reproduce them. Nothing here is required to build TelegArm itself — the DLLs are committed.

| Shipped file | PE machine | Size | Built |
|---|---|---|---|
| `TelegArm/rlottie/ARM/rlottie.dll` | `0x01C4` ARMNT (ARM32 Thumb-2) | 577 KB | 2026-06-17 |
| `TelegArm/rlottie/x86/rlottie.dll` | `0x014C` x86 | 337 KB | 2026-06-17 |

Upstream: **https://github.com/Samsung/rlottie** — MIT licensed (see `THIRD-PARTY-NOTICES.txt`,
component 10). TelegArm is GPL-3.0-only; MIT is compatible and imposes only the notice requirement,
which the notices file satisfies.

> **TODO — pin the provenance.** The exact upstream commit/tag these were built from was not recorded
> at build time. Before the next rebuild, capture `git rev-parse HEAD` of the rlottie checkout and
> record it here. Treat the current DLLs as "built from Samsung/rlottie master, 2026-06-17".

## Why we build it ourselves

1. **No upstream Windows-ARM32 binary exists.** The device target is Windows RT 8.1 / Windows 10
   ARM32, which upstream does not publish for.
2. **The static CRT is mandatory on RT.** A stock MSVC build links the *dynamic* CRT
   (`vcruntime140.dll`, `msvcp140.dll` + the UCRT `api-ms-win-crt-*` set). **Windows RT 8.1 has no
   ARM32 VC++ redistributable** — one has never been published — so a dynamic-CRT build fails to load
   with either a "msvcp140.dll is missing" loader dialog or a silent `DllNotFoundException`. Building
   with the **static** CRT folds the runtime into the DLL and removes the dependency entirely. This is
   the change that made animated `.tgs` stickers work on a clean RT 8.1 device (verified 2026-06-17).
3. rlottie is the practical `.tgs`/Lottie path here at all because **SkiaSharp 2.88.x ships
   `win-x86` / `win-x64` / `win-arm64` natives but no `win-arm` (32-bit)**, so SkiaSharp.Skottie
   `DllNotFound`s on ARM32.

## Build recipe

Requires CMake + the MSVC toolchain with the ARM32 (`ARM`) target installed.

```bash
git clone https://github.com/Samsung/rlottie.git
cd rlottie
```

**ARM32 (the RT device target):**
```bash
cmake -B build-arm -A ARM ^
      -DBUILD_SHARED_LIBS=ON ^
      -DCMAKE_POLICY_DEFAULT_CMP0091=NEW ^
      -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded ^
      -DCMAKE_SYSTEM_VERSION=6.3
cmake --build build-arm --config Release
```

**x86 (the desktop/dev target):**
```bash
cmake -B build-x86 -A Win32 ^
      -DBUILD_SHARED_LIBS=ON ^
      -DCMAKE_POLICY_DEFAULT_CMP0091=NEW ^
      -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
cmake --build build-x86 --config Release
```

Flag notes:
- `-A ARM` selects the 32-bit ARM target (**not** `ARM64`).
- `CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` = `/MT` (static CRT). It only takes effect together with
  `CMAKE_POLICY_DEFAULT_CMP0091=NEW` — without the policy, CMake ignores the variable and you silently
  get a dynamic-CRT DLL that dies on the device.
- `CMAKE_SYSTEM_VERSION=6.3` targets the Windows 8.1 SDK (RT is 8.1).

## Install

Copy **only the DLL** to the matching folder — the `.lib` (import library) and `.exp` (linker export
file) are deliberately **not** kept: TelegArm calls rlottie purely through P/Invoke
(`[DllImport("rlottie", CallingConvention = CallingConvention.Cdecl)]` in `TelegArm/Helpers/RLottie.cs`),
so nothing ever links against it. They are gitignored.

```
build-arm/src/Release/rlottie.dll  ->  TelegArm/rlottie/ARM/rlottie.dll
build-x86/src/Release/rlottie.dll  ->  TelegArm/rlottie/x86/rlottie.dll
```

`TelegArm.csproj` copies both to the output tree, and `Helpers/NativeLibraries.cs` calls
`SetDllDirectory` for the right one at startup based on `PROCESSOR_ARCHITECTURE` (an x86 process
reports `x86` even under WOW64; ARM32 reports `ARM`).

## Verify before shipping

```powershell
# PE machine must be 0x01C4 (ARMNT) for ARM, 0x014C for x86
$b=[IO.File]::ReadAllBytes('TelegArm\rlottie\ARM\rlottie.dll')
$pe=[BitConverter]::ToInt32($b,0x3C); '0x{0:X4}' -f [BitConverter]::ToUInt16($b,$pe+4)
```

Then confirm the CRT is really static — `dumpbin /dependents rlottie.dll` must **not** list
`VCRUNTIME140.dll` / `MSVCP140.dll`. If it does, `CMP0091` didn't apply and the DLL will not load on RT.

> ⚠ **The shipped x86 DLL FAILS this check — only the ARM32 one passes** (verified 2026-08-04 from the
> PE import tables: `rlottie/ARM/rlottie.dll` imports only `KERNEL32.dll` + `SHLWAPI.dll`, while
> `rlottie/x86/rlottie.dll` imports `MSVCP140.dll`, `VCRUNTIME140.dll` and 8 `api-ms-win-crt-*`). The
> recipe above is correct on paper, so the shipped x86 binary predates the `CMP0091` fix and was never
> re-cut. It is not fatal — x86 is the desktop/dev target, where the VC++ 2015-2022 redist is usually
> present — but on a clean desktop without it `LoadLibrary` fails, and because `NativeLibraries.cs:33`
> sets `SEM_FAILCRITICALERRORS` the failure is **silent**: animated `.tgs` stickers simply disappear
> with no error. Rebuild x86 with the recipe above before trusting this section again.

The C API we bind is `inc/rlottie_capi.h` (undecorated cdecl): `lottie_animation_from_data`,
`_get_totalframe`, `_get_framerate`, `_render`, `_destroy`. The render buffer is **BGRA
premultiplied**, which maps directly onto GDI+ `Format32bppPArgb` with no conversion.
