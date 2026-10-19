import subprocess, sys, re
PS = r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
r = subprocess.run([PS, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', sys.argv[1]],
                   capture_output=True, timeout=60)
out = (r.stdout or b'').decode('utf-8', errors='replace')
ns = re.findall(r'"nodes":\s*(-?\d+)', out)
print('PYBRIDGE nodes:', ns[:6] if ns else '(none)')
