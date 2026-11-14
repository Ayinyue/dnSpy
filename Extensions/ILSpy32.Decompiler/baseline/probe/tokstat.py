import collections, sys

# Aggregates (text, color) -> reference-rate statistics across the full corpus for both
# engines, and reports pairs where 2.3 attaches references but 3.2 doesn't (or where
# colors disagree for the same token text). This is the mechanism that catches systemic
# reference/color gaps regardless of which sample they appear in.

T23 = sys.argv[1] if len(sys.argv) > 1 else r'C:/Users/墨眉/AppData/Local/Temp/m3probe/full-cs/23.tsv'
T32 = sys.argv[2] if len(sys.argv) > 2 else r'C:/Users/墨眉/AppData/Local/Temp/m3probe/full-cs/32.tsv'

def load(path):
    stats = collections.defaultdict(lambda: [0, 0, 0])  # total, withRef, withoutRef
    refTypes = collections.defaultdict(collections.Counter)
    for line in open(path, encoding='utf-8', errors='replace'):
        parts = line.rstrip('\n').split('\t')
        if len(parts) < 5:
            continue
        text, color, hasRef, refType = parts[1], parts[2], parts[3], parts[4]
        key = (text, color)
        stats[key][0] += 1
        if hasRef == '1':
            stats[key][1] += 1
            refTypes[key][refType] += 1
        else:
            stats[key][2] += 1
    return stats, refTypes

s23, r23 = load(T23)
s32, r32 = load(T32)

common = set(s23) & set(s32)
print(f'tokens: 23={sum(v[0] for v in s23.values())} 32={sum(v[0] for v in s32.values())} pairs={len(s23)}/{len(s32)} common={len(common)}')

# 1) color mismatches for the same token text (regardless of ref)
print('--- COLOR MISMATCH (same token text, different dominant color) ---')
color23 = collections.defaultdict(collections.Counter)
color32 = collections.defaultdict(collections.Counter)
for (text, color), v in s23.items():
    color23[text][color] += v[0]
for (text, color), v in s32.items():
    color32[text][color] += v[0]
mismatches = 0
for text in set(color23) & set(color32):
    c23 = color23[text].most_common(1)[0][0]
    c32 = color32[text].most_common(1)[0][0]
    if c23 != c32:
        n = min(color23[text][c23], color32[text][c32])
        mismatches += 1
        print(f'  {text!r}: 23={c23} 32={c32} (n~{n})')
print(f'  total color mismatches: {mismatches}')

# 2) reference-rate gaps: 2.3 attaches refs, 3.2 doesn't (or vice versa)
print('--- REF-RATE GAPS (2.3 refs but 3.2 missing, or inverse) ---')
rows = []
for key in common:
    t23, r23c, _ = s23[key]
    t32, r32c, _ = s32[key]
    if t23 < 5 or t32 < 5:
        continue
    rate23, rate32 = r23c / t23, r32c / t32
    if rate23 >= 0.8 and rate32 <= 0.2:
        rows.append(('23-only', key, t23, rate23, t32, rate32, dict(r23[key])))
    elif rate32 >= 0.8 and rate23 <= 0.2:
        rows.append(('32-only', key, t32, rate32, t23, rate23, dict(r32[key])))
for kind, key, nA, rateA, nB, rateB, types in sorted(rows, key=lambda x: -min(x[3], x[5])):
    print(f'  {kind} {key[0]!r} [{key[1]}]: A-rate={rateA:.0%} B-rate={rateB:.0%} refs={dict(types)}')

# 3) color-only-in-one-engine tokens (structural differences)
only23 = set(s23) - set(s32)
only32 = set(s32) - set(s23)
print(f'--- pairs only in 23: {len(only23)}, only in 32: {len(only32)} ---')
byCount = collections.Counter()
for k in only23:
    byCount[(k[1], '23')] += s23[k][0]
for k in only32:
    byCount[(k[1], '32')] += s32[k][0]
for k, n in byCount.most_common(15):
    print(f'  {n:7d} {k}')
