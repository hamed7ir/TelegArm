# libVLC — what we bundle, where it came from, and how to re-cut it

TelegArm bundles the **native libVLC runtime** as two per-architecture zips at the repo root,
`libvlc-arm32.zip` and `libvlc-x86.zip`. `Core/VlcBootstrap.cs` extracts the matching-arch one into a
`libvlc/` folder beside the executable on first run.

This file exists because the two archives are **not the same libVLC**, are **not both official**, and
one of them is **not reproducible**. Written 2026-08-04 (BATCH-TA-2/L5). Everything below was verified
this session by reading the archives themselves — version strings and build paths out of
`libvlccore.dll`, per-entry timestamps out of the zip directory, and SHA-256 over the whole file.

> **This is a corresponding-source / provenance record, not a build recipe.** We do not compile
> libVLC. Both archives are cut down from binaries produced elsewhere. The obligation these notes
> serve is being able to say exactly *what* we ship and *where its source is*.

## The two archives at a glance

| | `libvlc-arm32.zip` | `libvlc-x86.zip` |
|---|---|---|
| libVLC version | **3.0.20** | **3.0.23** |
| Changeset | `3.0.20-0-g6f0d0ab126` | `3.0.23-2-0-g79128878dd` |
| Origin | **UNOFFICIAL** third-party cross-build | VideoLAN's own GitLab CI |
| Build path in binary | `/home/rwan007/vlc/vlc-3.0.20/…` | `/builds/videolan/vlc/…` |
| Toolchain | LLVM 13.0, `armv7-w64-mingw32` | gcc 6.4.0, `i686-w64-mingw32` |
| Size | 45,860,731 bytes | 60,887,682 bytes |
| SHA-256 | `9bf63e0ac4e3701f936a732de3cc198c66858e249bf9e914f4b7abcf3b21349c` | `0c23ff6f863bf5cdec28f95b5501a0c9e65e5876b40f7b253f3431d5ef8fec47` |
| Files | 358 | 373 |
| Plugin DLLs | 354 | 366 |
| Uniform build? | **yes** — every file 2023-11-02 | **no** — see below |
| Reproducible? | **yes**, from the upstream archive | **no** |

## ARM32 — unofficial, but stock and verified

The ARM32 build is the whole reason TelegArm can play media on Windows RT: VideoLAN publishes no
32-bit ARM Windows build, so this one comes from a third party who cross-compiled 3.0.20 with an
LLVM 13 `armv7-w64-mingw32` toolchain. The build path `/home/rwan007/vlc/vlc-3.0.20/` is still
embedded in `libvlccore.dll`.

**It is stock, not subsetted or patched.** The upstream archive it was cut from is retained on the
dev box as `D:\arm\vlc-3.0.20-Windows_11_ARM.7z` (25,288,141 bytes, 2023-11-02). `libvlc.dll` and
`libvlccore.dll` are SHA-256 identical between that archive and our zip, and the plugin tree is
356 files in both. All 358 entries in our zip carry the same 2023-11-02 timestamp — one build, no
overlays.

Contents: `libvlc/libvlc.dll`, `libvlc/libvlccore.dll`, 354 plugin DLLs under `libvlc/plugins/**`,
and 2 libbluray jars (`libbluray-j2se-1.3.2.jar`, `libbluray-awt-j2se-1.3.2.jar`). **No
`plugins.dat`** — VLC regenerates the plugin cache on the device at first run.

### The cut-down recipe (ARM32)

Extract `vlc-3.0.20-Windows_11_ARM.7z` and keep **only**:

```
vlc-3.0.20/libvlc.dll        ->  libvlc/libvlc.dll
vlc-3.0.20/libvlccore.dll    ->  libvlc/libvlccore.dll
vlc-3.0.20/plugins/**        ->  libvlc/plugins/**        (NOT subsetted — all 356 entries)
```

Everything else upstream ships is dropped: `vlc.exe`, `axvlc.dll`, `npvlc.dll`,
`vlc-cache-gen.exe`, `sdk/`, `skins/`, `lua/`, `hrtfs/` — **and, regrettably, `COPYING.txt`,
`AUTHORS.txt`, `NEWS.txt`, `README.txt`.** Dropping `COPYING.txt` is a compliance defect: the GPL-2.0
text it contained is now reproduced in the repo-root `THIRD-PARTY-NOTICES.txt` instead, and that file
ships with every installed copy. **When either zip is next re-cut, put upstream's `COPYING.txt` back
inside the bundle.**

### Corresponding source (ARM32)

The changeset `3.0.20-0-g6f0d0ab126` sits exactly on the `3.0.20` tag, so the corresponding source is
unmodified upstream:

- <https://code.videolan.org/videolan/vlc/-/tree/3.0.20>
- <https://download.videolan.org/pub/videolan/vlc/3.0.20/vlc-3.0.20.tar.xz>

## x86 — official build, unofficial archive

`libvlccore.dll` reports 3.0.23 / `3.0.23-2-0-g79128878dd`, built in `/builds/videolan/vlc/` with
gcc 6.4.0 and `--with-breakpad=https://win.crashes.videolan.org`. That part is a genuine VideoLAN
win32 release build.

**The archive around it is not.** Per-entry timestamps show it was assembled by overlaying 3.0.23
onto an older tree and never cleaned:

| Timestamp | Files | What |
|---|---|---|
| 2025-12-31 | 365 | the actual 3.0.23 build |
| 2024-06-08 | 5 | **stale leftovers** — `liba52_plugin.dll`, `libdca_plugin.dll`, `liblibmpeg2_plugin.dll` (all three report **FileVersion 3.0.21**), plus `libbluray-j2se-1.3.2.jar` and `libbluray-awt-j2se-1.3.2.jar` |
| 2025-12-23 | 2 | `libbluray-j2se-1.4.0.jar`, `libbluray-awt-j2se-1.4.0.jar` |
| 2026-04-12 | 1 | `plugins.dat`, **regenerated locally on this dev box** |

So the archive ships **three 3.0.21 plugins inside a 3.0.23 tree**, **two conflicting libbluray
versions side by side** (1.3.2 *and* 1.4.0), and a locally-generated plugin cache.

**No published VLC release matches this file set. `libvlc-x86.zip` is UNREPRODUCIBLE as shipped** —
that is stated plainly here rather than papered over. It is not a correctness problem in practice
(the app runs), but nobody can verify this archive against upstream, which is exactly what a
provenance record is supposed to make possible.

### Re-cutting x86 (recommended, not yet done)

Take a pristine official 3.0.23 win32 archive from
<https://download.videolan.org/pub/videolan/vlc/3.0.23/win32/> and keep only `libvlc.dll`,
`libvlccore.dll` and `plugins/**` — no overlay, no locally-built `plugins.dat`, one uniform
timestamp. Then bump `BundleVersion` in `TelegArm/Core/VlcBootstrap.cs:22` from `"1"` to `"2"` so
existing installs re-extract instead of reusing the stale `libvlc/` folder. Put upstream's
`COPYING.txt` in as well.

### Corresponding source (x86)

- <https://code.videolan.org/videolan/vlc/-/tree/3.0.23>
- <https://download.videolan.org/pub/videolan/vlc/3.0.23/vlc-3.0.23.tar.xz>

## Licensing — the bundle is GPL, not just LGPL

`libvlc.dll` and `libvlccore.dll` are LGPL-2.1-or-later. **The bundled plugin set is not.** Both
archives carry plugins built against GPL-licensed upstream libraries — `liba52`, `libdca`,
`libdvdread`, `libdvdnav`, `libpostproc`, `libgoom`, `libsid`, `libmad`, `libx264`, `libx26410b`,
`libx265` in both, plus `libfaad` and `libprojectm` in x86 only. Upstream's own Windows distribution
ships a top-level `COPYING.txt` containing the **GPL version 2**, which is consistent with that.

**The bundle as distributed is therefore GPL-2.0-or-later**, and TelegArm's notices say so.
GPL-2.0-or-later upgrades to GPL-3, so it is conveyed cleanly under TelegArm's own GPL-3.0-only
licence.

**Every GPL plugin is "or-later"; none is GPL-2.0-only.** This was worth establishing rather than
assuming, because a GPL-2.0-*only* component would be a real licence conflict with a GPL-3.0-only
application, not merely a documentation gap. The one candidate — `libfaad` (FAAD2) — was checked
directly against upstream's `COPYING` and `README`, which grant "either version 2 of the License, or
(at your option) any later version". An earlier internal audit had recorded FAAD2 as GPL-2.0-only;
**that is refuted.** FAAD2's Nero addendum forbids *non-GPL* use and requires the attribution string
`Code from FAAD2 is copyright (c) Nero AG, www.nero.com`, which the notices now carry.

Note that the shipped plugin binaries **do not** embed their upstream library's licence — the only
licence string inside them is VLC's own module-wrapper header ("Licensed under the terms of the GNU
Lesser General Public License, version 2.1 or later"), which describes the VLC glue code and not the
statically-linked codec. Per-plugin attributions therefore come from the upstream projects, except
FAAD2 as noted. See `THIRD-PARTY-NOTICES.txt` component 4 for the full table.

## Verify an archive

```powershell
# identity + size
Get-FileHash .\libvlc-arm32.zip -Algorithm SHA256

# version / changeset / build path baked into libvlccore.dll
$z=[IO.Compression.ZipFile]::OpenRead((Resolve-Path .\libvlc-arm32.zip))
$e=$z.GetEntry('libvlc/libvlccore.dll'); $s=$e.Open()
$ms=New-Object IO.MemoryStream; $s.CopyTo($ms); $z.Dispose()
[Text.Encoding]::ASCII.GetString($ms.ToArray()) -match '3\.0\.\d+-\d+-g[0-9a-f]+'; $Matches[0]
```

A uniform archive has one timestamp across every entry. If a re-cut ever produces mixed timestamps
again, it has been overlaid rather than rebuilt — see the x86 table above for what that looks like.
