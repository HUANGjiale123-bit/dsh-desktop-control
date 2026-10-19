#!/usr/bin/env python3
"""UIA 读取桥 —— 由 Node（非 .NET）直接启动，绕开 .NET 进程树。

── 为什么需要它 ──

实测数据（同一个 SuYou 窗口、同一秒）：

    父进程 bash / cmd / python（都非 .NET）→ PowerShell 读到 39 / 38 / 37 / 37 / 29
    父进程 .NET（dsc.exe / DpiTest / JobTest）→ 读到 5 / 4 / 3 / 2 / -1

而且影响会沿整条进程树传下去：

    .NET → cmd.exe → powershell.exe   → 5
    .NET → python.exe → powershell.exe → 5

所以桥不能由 dsc.exe（.NET）启动，必须由 Node 直接启动。

── 协议 ──

stdin 一行 JSON  →  stdout 一行 JSON。

请求：
    {"hwnd": 67072, "maxNodes": 200, "timeoutMs": 8000}

响应：
    {"ok": true, "hwnd": 67072, "nodes": 29, "items": [...]}
    {"ok": false, "error": "..."}

── 为什么用 PowerShell 而不是 pythoncom ──

因为已验证 PowerShell 能读到 29，而 pythoncom 需要额外装 pywin32。
PowerShell 是 Windows 自带的，不需要任何依赖。
python 在这里只是"非 .NET 的启动器 + JSON 转接"。"""

import json
import re
import subprocess
import sys

# stdout 必须强制 UTF-8。
# 实测踩到：Windows 上的 python 默认用 GBK 编码 stdout，而树里的中文
# （窗口名、按钮文字）会直接抛 UnicodeEncodeError: 'gbk' codec can't encode。
# 表现是桥退出码 1、stdout 空 —— 调用方只能看到"读不到"，看不到真正的原因。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

PS = r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"

SCRIPT_TEMPLATE = r"""
$ErrorActionPreference = 'Stop'

# ── 输出必须是 UTF-8 ──
#
# 不设这一行的时候，PowerShell 的 stdout 用系统 ANSI（中文 Windows 上是 GBK），
# 而桥那边按 UTF-8 解码 —— 于是每个中文窗口名都变成一串 �。
# 表现是"读到了 62 个节点，但一个名字都不认识"，
# 而且 JSON 结构完全正常，所以看起来不像编码问题。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# 枚举子窗口 —— Chromium 的内容在 Chrome_RenderWidgetHostHWND 这类子窗口里。
# 顶层窗口只给外壳（实测 dsc.exe 读顶层是 0 个节点）。
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class DscBridgeEnum {{
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}}
"@
$target = [IntPtr][long]{hwnd}
$cands = New-Object System.Collections.ArrayList
[void]$cands.Add($target)
$candNames = New-Object System.Collections.ArrayList
[void]$candNames.Add("(top)")
$cb = [DscBridgeEnum+EnumProc]{{
  param($hh,$ll)
  $sbn = New-Object System.Text.StringBuilder 256
  [void][DscBridgeEnum]::GetClassNameW($hh, $sbn, 256)
  $cls = $sbn.ToString()
  if ($cls -match 'RenderWidgetHost|WRY_WEBVIEW|Chrome_WidgetWin_1|WebView2|Internet Explorer_Server') {{
    [void]$script:cands.Add($hh)
    [void]$script:candNames.Add($cls)
  }}
  return $true
}}
[void][DscBridgeEnum]::EnumChildWindows($target, $cb, [IntPtr]::Zero)

# 对每个候选读一次，取节点最多的
$best = $null
$bestHwnd = $target
$bestName = "(top)"
for ($ci = 0; $ci -lt $cands.Count; $ci++) {{
  $h = $cands[$ci]
  try {{
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    if ($el -eq $null) {{ continue }}
    $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                       [System.Windows.Automation.Condition]::TrueCondition)
    if ($all -ne $null -and $all.Count -gt 0) {{
      if ($best -eq $null -or $all.Count -gt $best.Count) {{
        $best = $all
        $bestHwnd = $h
        $bestName = $candNames[$ci]
        $bestEl = $el
      }}
    }}
  }} catch {{ }}
}}
if ($best -eq $null) {{
  Write-Output '{{"ok":false,"error":"all candidates empty"}}'
  exit 0
}}
$el = $bestEl
$all = $best
if ($el -eq $null) {{ Write-Output '{{"ok":false,"error":"FromHandle null"}}'; exit 0 }}
$all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                   [System.Windows.Automation.Condition]::TrueCondition)
$items = @()
$n = 0
foreach ($x in $all) {{
  if ($n -ge {maxNodes}) {{ break }}
  $ct = ''; try {{ $ct = $x.Current.ControlType.ProgrammaticName }} catch {{ }}
  $nm = ''; try {{ $nm = $x.Current.Name }} catch {{ }}
  $l=0;$t=0;$w=0;$hh=0
  try {{ $b = $x.Current.BoundingRectangle; $l=[int]$b.Left; $t=[int]$b.Top; $w=[int]$b.Width; $hh=[int]$b.Height }} catch {{ }}
  $items += @{{ type = $ct; name = $nm; l = $l; t = $t; w = $w; h = $hh }}
  $n++
}}
@{{
  ok = $true
  hwnd = {hwnd}
  viaHwnd = [long]$bestHwnd
  viaClassName = $bestName
  isControlElement = $el.Current.IsControlElement
  isContentElement = $el.Current.IsContentElement
  name = $el.Current.Name
  className = $el.Current.ClassName
  nodes = $all.Count
  items = $items
}} | ConvertTo-Json -Depth 6 -Compress
"""


def read_tree(hwnd: int, max_nodes: int = 200, timeout_ms: int = 8000) -> dict:
    script = SCRIPT_TEMPLATE.format(hwnd=hwnd, maxNodes=max_nodes)
    try:
        r = subprocess.run(
            [PS, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-Command", script],
            capture_output=True, timeout=max(1.0, timeout_ms / 1000.0),
        )
    except subprocess.TimeoutExpired:
        return {"ok": False, "error": "timeout"}

    out = (r.stdout or b"").decode("utf-8", errors="replace").strip()
    if not out:
        err = (r.stderr or b"").decode("utf-8", errors="replace").strip()
        return {"ok": False, "error": "no output", "stderr": err[:500]}
    try:
        # ConvertTo-Json 有时会带 BOM 或者前后空白
        out = out.lstrip("\ufeff").strip()
        return json.loads(out)
    except Exception as e:
        return {"ok": False, "error": "bad json: %s" % e, "raw": out[:500]}


def main():
    # 单次模式：命令行给 hwnd
    if len(sys.argv) > 1:
        hwnd = int(sys.argv[1])
        # 第二个参数是 maxNodes。原来这里只读 hwnd，于是调用方传的
        # 上限被静默忽略 —— 请求 8 个会拿回全部 62 个。
        max_nodes = int(sys.argv[2]) if len(sys.argv) > 2 else 300
        result = read_tree(hwnd, max_nodes)
        sys.stdout.write(json.dumps(result, ensure_ascii=False) + "\n")
        return 0 if result.get("ok") else 1

    # 常驻模式：stdin 一行一个请求
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except Exception as e:
            sys.stdout.write(json.dumps({"ok": False, "error": "bad request: %s" % e}) + "\n")
            sys.stdout.flush()
            continue
        hwnd = int(req.get("hwnd", 0))
        result = read_tree(
            hwnd,
            int(req.get("maxNodes", 200)),
            int(req.get("timeoutMs", 8000)),
        )
        sys.stdout.write(json.dumps(result, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
