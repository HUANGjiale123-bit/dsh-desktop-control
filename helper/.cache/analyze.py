import io, sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

p = 'src/Probe.cs'
lines = io.open(p, encoding='utf-8').read().split('\n')

BACKSLASH = chr(92)
depth = 0
events = []
for i, l in enumerate(lines):
    j = 0
    n = len(l)
    while j < n:
        ch = l[j]
        if ch == '"':
            j += 1
            while j < n:
                if l[j] == BACKSLASH:
                    j += 2
                    continue
                if l[j] == '"':
                    break
                j += 1
            j += 1
            continue
        if ch == '/' and j + 1 < n and l[j + 1] == '/':
            break
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
        j += 1

    st = l.strip()
    if (st.startswith('internal static class') or st.startswith('internal sealed class')
            or st.startswith('private static int ') or st.startswith('private static void ')
            or st.startswith('internal static Dictionary') or st.startswith('private static List')
            or st.startswith('private static string ')):
        events.append((i + 1, depth, st[:55]))

print('  方法/类位置（括号深度）:')
for ln, d, txt in events:
    print('  %5d  depth=%-2d  %s' % (ln, d, txt))
print()
print('  最终 depth:', depth, '（应该是 2：namespace + class）')
