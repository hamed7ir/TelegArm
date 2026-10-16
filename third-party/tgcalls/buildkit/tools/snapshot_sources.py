"""Zip the exact source trees the tgcalls/tg_owt packages were built from (working trees: patches APPLIED, no .git).

Usage: python -I snapshot_sources.py <out.zip>
Paths inside the zip mirror D:/tgcalls/ (so the build kit's D:/tgcalls/... paths work after extracting to D:/tgcalls).
Excluded: .git dirs, openh264/res (test bitstreams), ffmpeg-rt/deps/ffmpeg-8.1.2 (extracted tree; the verified
tarball next to it is included and fetch-deps.sh re-extracts it), build outputs.
"""
import os
import sys
import zipfile

ROOT = 'D:/tgcalls'
TREES = ['tg_owt', 'tgcalls', 'meta', 'deps/boringssl', 'deps/opus', 'deps/libvpx', 'deps/openh264', 'deps/mozjpeg',
         'deps/zlib', 'deps/rnnoise', 'deps/nasm', 'deps/ffmpeg-rt']
SKIP_DIRS = {'.git'}
SKIP_PATHS = {'deps/openh264/res', 'deps/ffmpeg-rt/deps/ffmpeg-8.1.2', 'deps/ffmpeg-rt/build'}
out = sys.argv[1]
n = size = 0
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for tree in TREES:
        for root, dirs, files in os.walk(os.path.join(ROOT, tree)):
            rel_root = os.path.relpath(root, ROOT).replace('\\', '/')
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS and f'{rel_root}/{d}' not in SKIP_PATHS]
            for f in files:
                p = os.path.join(root, f)
                if os.path.islink(p):
                    continue
                arc = f'tgcalls-src/{rel_root}/{f}'
                z.write(p, arc)
                n += 1
                size += os.path.getsize(p)
    z.write(os.path.join(ROOT, 'package/SOURCES.txt'), 'tgcalls-src/SOURCES.txt')
print(out, n, 'files', size // (1 << 20), 'MB uncompressed', os.path.getsize(out) // (1 << 20), 'MB zip')
