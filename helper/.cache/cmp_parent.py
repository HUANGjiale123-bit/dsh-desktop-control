import io, os, re, subprocess, sys, glob
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

# 找最新的脚本
files = sorted(glob.glob(r'D:\dsh workspace\.dsc-psread\psread-*.ps1'),
               key=os.path.getmtime, reverse=True)
script = files[0]
print('  脚本:', script)

PS = r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'

def count_nodes(text):
    m = re.findall(r'"nodes"\s*:\s*(-?\d+)', text)
    if m:
        return m
    m = re.findall(r'\\"nodes\\"\s*:\s*(-?\d+)', text)
    return m

def run(label, exe, args, **kw):
    print()
    print('  ════ ' + label + ' ════')
    try:
        r = subprocess.run([exe] + args, capture_output=True, timeout=60, **kw)
        out = (r.stdout or b'').decode('utf-8', errors='replace')
        err = (r.stderr or b'').decode('utf-8', errors='replace')
        ns = count_nodes(out)
        print('    字节:', len(out), ' 退出码:', r.returncode)
        print('    nodes:', ns[:8] if ns else '(没找到)')
        if not ns:
            print('    stdout 前 200:', out[:200].replace('\n', ' '))
            if err:
                print('    stderr 前 200:', err[:200].replace('\n', ' '))
    except subprocess.TimeoutExpired:
        print('    超时')
    except Exception as e:
        print('    失败:', e)

# A. cmd.exe 启动
bat = r'D:\dsh workspace\.dsc-psread\run2.bat'
with io.open(bat, 'w', encoding='gbk') as f:
    f.write('@echo off\r\n')
    f.write('"' + PS + '" -NoProfile -ExecutionPolicy Bypass -File "' + script + '"\r\n')
run('cmd.exe /c run2.bat', r'C:\Windows\System32\cmd.exe', ['/c', bat])

# B. 直接启动（python 的 subprocess，父进程是 python.exe，不是 .NET）
run('python.exe 直接启动', PS, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script])
