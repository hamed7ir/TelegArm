"""Assemble one per-architecture package of tgcalls + tg_owt + dependency static libraries.

Usage: python -I package.py <arch: arm32|x86> <out-dir>
Layout:  lib/      static libraries (tgcalls, tg_owt, BoringSSL, FFmpeg, opus, vpx, openh264, jpeg, rnnoise, zlib)
         include/  tgcalls/ (repo layout: include/tgcalls/<headers>), tg_owt/ (src tree headers incl. abseil, libyuv),
                   deps/ (ffmpeg, opus, vpx, wels, jpeg, zlib, rnnoise headers), boringssl/
         bin/      tgcalls_link_test.exe (+ .pdb, .map)
         BUILD-INFO.txt, SHA256SUMS.txt
Header trees are copied as .h/.hpp/.inc only. Writes nothing outside <out-dir>.
"""
import hashlib
import os
import shutil
import sys

arch, out = sys.argv[1], sys.argv[2]
B = 'D:/tgcalls/build'
P = 'D:/tgcalls/prefix/' + arch
if os.path.exists(out):
    sys.exit('refusing to overwrite existing ' + out)

libs = {
    'tgcalls.lib': f'{B}/tgcalls-{arch}/tgcalls.lib',
    'tg_owt.lib': f'{B}/tg_owt-{arch}/tg_owt.lib',
    'ssl.lib': f'{B}/boringssl-{arch}/ssl.lib',
    'crypto.lib': f'{B}/boringssl-{arch}/crypto.lib',
    'avformat.lib': f'{P}/lib/avformat.lib',
    'avcodec.lib': f'{P}/lib/avcodec.lib',
    'swresample.lib': f'{P}/lib/swresample.lib',
    'swscale.lib': f'{P}/lib/swscale.lib',
    'avutil.lib': f'{P}/lib/avutil.lib',
    'opus.lib': f'{P}/lib/opus.lib',
    'vpx.lib': f'{P}/lib/libvpx.a',  # GNU-format archive from libvpx's make; lld-link/link.exe read it as-is
    'openh264.lib': f'{P}/lib/openh264.lib',
    'jpeg-static.lib': f'{P}/lib/jpeg-static.lib',
    'rnnoise.lib': f'{P}/lib/rnnoise.lib',
    'zlibstatic.lib': f'{P}/lib/libzs.lib',
}
os.makedirs(out + '/lib')
for name, src in libs.items():
    shutil.copy2(src, f'{out}/lib/{name}')


def copy_headers(src_root, dst_root, skip_dirs=()):
    n = 0
    for root, dirs, files in os.walk(src_root):
        dirs[:] = [d for d in dirs if d not in skip_dirs and not d.startswith('.')]
        for f in files:
            if f.endswith(('.h', '.hpp', '.inc')):
                rel = os.path.relpath(os.path.join(root, f), src_root)
                os.makedirs(os.path.dirname(os.path.join(dst_root, rel)), exist_ok=True)
                shutil.copy2(os.path.join(root, f), os.path.join(dst_root, rel))
                n += 1
    return n


counts = {
    'tgcalls': copy_headers('D:/tgcalls/tgcalls/tgcalls', out + '/include/tgcalls/tgcalls', ('v2wasm',)),
    'tg_owt': copy_headers('D:/tgcalls/tg_owt/src', out + '/include/tg_owt',
                           ('test', 'testsupport', 'examples', 'benchmark', 'googletest', 'glog', 'unittests')),
    'deps': copy_headers(P + '/include', out + '/include/deps'),
    'boringssl': copy_headers('D:/tgcalls/deps/boringssl/include', out + '/include/boringssl'),
}
os.makedirs(out + '/bin')
for f in ('tgcalls_link_test.exe', 'tgcalls_link_test.pdb', 'tgcalls_link_test.map'):
    src = f'{B}/tgcalls-{arch}/{f}'
    if os.path.exists(src):
        shutil.copy2(src, f'{out}/bin/{f}')
shutil.copy2(f'D:/tgcalls/package/BUILD-INFO-{arch}.txt', out + '/BUILD-INFO.txt')
os.makedirs(out + '/src')
shutil.copy2('D:/tgcalls/cmake/tgcalls/tgcalls_link_test.cpp', out + '/src/tgcalls_link_test.cpp')

sums = []
for root, _, files in os.walk(out):
    for f in sorted(files):
        p = os.path.join(root, f)
        if f == 'SHA256SUMS.txt' or '/include/' in p.replace('\\', '/'):
            continue
        h = hashlib.sha256(open(p, 'rb').read()).hexdigest()
        sums.append('%s  %s' % (h, os.path.relpath(p, out).replace('\\', '/')))
with open(out + '/SHA256SUMS.txt', 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(sums) + '\n')
print(arch, 'headers', counts, 'libs', len(libs))
