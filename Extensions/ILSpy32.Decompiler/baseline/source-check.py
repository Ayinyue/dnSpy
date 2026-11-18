"""Source-vs-decompilation check.

The corpus gates diff 3.2 against dnSpy 2.3. That tells you where the two
engines disagree, not which one is right - audit 8.67 and 8.70 both contain
cases where 2.3 is wrong and aligning to it would make the output worse.
This script uses the actual C# source (which the repo ships) as the oracle.

Usage:
    py source-check.py <dumpDir> <sourceDir> [--limit N] [--show M]

<dumpDir> is the output of: baseline dump --impl ilspy32 --lang cs --all
For every dumped type it finds the source file(s) mentioning the type name and
reports source *member declarations* whose name never shows up in the
decompilation - a missing name is a strong signal that the decompiler dropped
or renamed something.

Heuristic, not a parser: it exists to make "is this fix right?" answerable
without hand-reading output. False positives are expected and printed as-is.
"""
import os
import re
import sys
import glob

MEMBER_RE = re.compile(
    r'^[ \t]*'                      # indentation
    r'(?:\[[^\]]*\][ \t]*)*'        # attributes
    r'(?:(?:public|private|protected|internal|static|readonly|volatile|virtual|override|abstract|sealed|async|partial|new|extern|unsafe|const|event|ref|required|file)\s+)*'
    r'(?:[\w<>\[\],\.\?\+\*]+\s+)?'  # return type / event type
    r'(\w+)\s*'                     # (1) member name
    r'[\(\{=;]'                     # followed by ( { = or ;
)

KEYWORDS = {
    'if', 'for', 'foreach', 'while', 'switch', 'catch', 'return', 'using',
    'lock', 'get', 'set', 'add', 'remove', 'value', 'new', 'throw', 'yield',
    'try', 'break', 'continue', 'do', 'else', 'case', 'default', 'in', 'out',
    'var', 'string', 'int', 'uint', 'long', 'bool', 'object', 'void',
}


def type_key(file_name):
    """Strip the numeric prefix from a dump file name."""
    return re.sub(r'^\d+_', '', file_name)


def find_sources(source_dir, key, cache):
    """Source files whose text mentions the (sanitized) type name."""
    if key in cache:
        return cache[key]
    found = []
    for root, _dirs, files in os.walk(source_dir):
        if '\\obj\\' in root or '/obj/' in root or '\\bin\\' in root or '/bin/' in root:
            continue
        for f in files:
            if not f.endswith('.cs'):
                continue
            p = os.path.join(root, f)
            try:
                text = open(p, encoding='utf-8-sig', errors='ignore').read()
            except OSError:
                continue
            if key.split('.')[-1] in text:
                found.append(text)
    cache[key] = found
    return found


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    dump_dir, source_dir = sys.argv[1], sys.argv[2]
    limit = None
    if '--limit' in sys.argv:
        limit = int(sys.argv[sys.argv.index('--limit') + 1])

    cache = {}
    checked = 0
    flagged = 0
    for path in sorted(glob.glob(os.path.join(dump_dir, '*.txt'))):
        base = os.path.basename(path)
        key = type_key(os.path.splitext(base)[0])
        decompiled = open(path, encoding='utf-8-sig', errors='ignore').read()
        sources = find_sources(source_dir, key, cache)
        if not sources:
            continue
        checked += 1
        missing = []
        for src in sources:
            for line in src.split('\n'):
                # 只看类级成员（缩进 ≤ 1 个 tab）；方法体内的局部变量会被反编译器
                # 改名/消除，拿它们比对只会产生噪音。
                indent = len(line) - len(line.lstrip('\t'))
                if indent > 1:
                    continue
                stripped = line.strip()
                if stripped.startswith('//') or stripped.startswith('*') or stripped.startswith('/*'):
                    continue
                if stripped.startswith('Copyright'):
                    continue
                m = MEMBER_RE.match(line)
                if not m:
                    continue
                # 同文件里的其他类型声明不算本类型的成员
                if re.search(r'\b(class|struct|enum|interface)\s+\w+', line):
                    continue
                name = m.group(1)
                if not name or name in KEYWORDS:
                    continue
                if name in decompiled:
                    continue
                missing.append((name, line.strip()[:100]))
        if missing:
            flagged += 1
            print('=== %s (%d source file(s))' % (key, len(sources)))
            seen = set()
            for name, line in missing:
                if name in seen:
                    continue
                seen.add(name)
                print('   missing: %-28s %s' % (name, line))
        if limit and checked >= limit:
            break
    print('checked=%d flagged=%d' % (checked, flagged))
    return 1 if flagged else 0


if __name__ == '__main__':
    sys.exit(main())
