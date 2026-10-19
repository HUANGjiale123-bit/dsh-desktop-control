import io, os, re, subprocess, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

# 用两个不同的父进程启动同一个 .NET 程序（MiniDump.exe / MiniTest.exe），
# 看它读到的节点数。父进程换成 python 之后如果变好，说明不是 .NET 本身，
# 而是 bash 之外的环境差异。

TARGET = 67072  # Chrome_RenderWidgetHostHWND

def run(label, argv):
    print()
    print('  ==== ' + label + ' ====')
    try:
        r = subprocess.run(argv, capture_output=True, timeout=60)
        out = (r.stdout or b'').decode('utf-8', errors='replace')
        # 抽 nodes=N
        ns = re.findall(r'nodes\s*=\s*(-?\d+)', out)
        print('    退出码:', r.returncode)
        print('    nodes:', ns[:6] if ns else '(没找到)')
        if not ns:
            print('    输出前 200:', out[:200].replace('\n', ' | '))
    except Exception as e:
        print('    失败:', e)

HELPER = r'D:\dsh workspace\dsh-desktop-control\helper'

# A. python 启动 .NET 的 MiniDump（只读 RenderWidgetHost）
run('python 启动 MiniDump.exe (.NET)', [os.path.join(HELPER, r'.cache\MiniDump.exe'), str(66988)])

# B. python 启动 MiniTest.exe (.NET)，它列出所有子窗口的 nodes=
run('python 启动 MiniTest.exe (.NET)', [os.path.join(HELPER, r'.cache\MiniTest.exe'), str(66988)])
