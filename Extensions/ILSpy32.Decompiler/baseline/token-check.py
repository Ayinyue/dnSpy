"""Token-comment sanity check.

Audit 8.73 found a real bug where two method declarations in the same type were
printed with the SAME '// Token: 0x... RID: n' comment (the explicit interface
implementation and its public method). A duplicated RID inside one type means the
MethodDef annotation was bound to the wrong declaration.

Legit repeats do not exist: a property's accessor comment uses the accessor's own
RID, and nested types live in their own dump file.

Usage:
    py token-check.py <dumpDir> [--show N]

<dumpDir> is the output of: baseline dump --impl ilspy32 --lang cs --all
Exit code 1 if any duplicate is found.
"""
import glob
import os
import re
import sys

TOKEN_RE = re.compile(r'// (?:get|set|add|remove|method:\s*\S+\s+)?Token: 0x([0-9A-Fa-f]+) RID: (\d+)')


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    dump_dir = sys.argv[1]
    show = 10
    if '--show' in sys.argv:
        show = int(sys.argv[sys.argv.index('--show') + 1])

    bad = 0
    checked = 0
    examples = []
    for path in sorted(glob.glob(os.path.join(dump_dir, '*.txt'))):
        text = open(path, encoding='utf-8-sig', errors='ignore').read()
        seen = {}
        for m in TOKEN_RE.finditer(text):
            rid = m.group(2)
            seen[rid] = seen.get(rid, 0) + 1
        dups = {rid: n for rid, n in seen.items() if n > 1}
        if not dups:
            continue
        bad += 1
        checked += 1
        if len(examples) < show:
            examples.append((os.path.basename(path), dups))

    for name, dups in examples:
        print('=== %s' % name)
        for rid, n in sorted(dups.items(), key=lambda kv: -kv[1]):
            print('   RID %s printed %d times' % (rid, n))
    print('types_with_duplicates=%d' % bad)
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
