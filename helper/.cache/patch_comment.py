import io, sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

p = 'src/Probe.cs'
lines = io.open(p, encoding='utf-8', newline='').read().split('\n')

# 找到孤立的 ' *' 开头（注释块缺了 /* 那行）
for i, l in enumerate(lines):
    if l.strip() == '*' and i > 0 and lines[i - 1].strip() == '':
        # 在它前面插入 /* 那一行
        lines.insert(i, '        /* 找出真正的「内容 HWND」。')
        print('  在第 %d 行前补回注释开头' % (i + 1))
        break
else:
    print('  ✗ 没找到')

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
