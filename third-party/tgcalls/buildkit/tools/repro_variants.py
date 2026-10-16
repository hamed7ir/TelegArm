"""Re-run one ninja compile command with variants, sequentially, at BelowNormal priority (creationflags 0x4000).

Usage: python -I repro_variants.py <cmd-file> <workdir> <outdir>
Each variant: (name, replacements as (old, new) pairs, extra args). Prints exit code + the error lines.
"""
import os
import subprocess
import sys

cmd_file, workdir, outdir = sys.argv[1:4]
base = open(cmd_file, encoding='utf-8').read().strip()
os.makedirs(outdir, exist_ok=True)
fo = base.split('/Fo')[1].split()[0]
VARIANTS = [
    ('orig', [], ''),
    ('no-eh', [('/EHsc', '/EHs-c-')], ''),
    ('O1', [('/O2', '/O1')], ''),
    ('asm', [], '/clang:-S'),
]
for name, reps, extra in VARIANTS:
    c = base
    for old, new in reps:
        c = c.replace(old, new)
    out = os.path.join(outdir, name + ('.s' if extra == '/clang:-S' else '.obj')).replace('\\', '/')
    c = c.replace('/Fo' + fo, '/Fo' + out) + (' ' + extra if extra else '')
    if extra == '/clang:-S':
        c = c.replace('/Fo' + out, '/Fa' + out + ' /Fo' + out + '.obj')
    r = subprocess.run(c, cwd=workdir, capture_output=True, text=True, creationflags=0x4000)
    errs = [l for l in (r.stdout + r.stderr).splitlines() if 'error' in l]
    print('%-6s rc=%d %s' % (name, r.returncode, ' | '.join(errs[:3])))
