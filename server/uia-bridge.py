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

读树：
    {"hwnd": 67072, "maxNodes": 200, "timeoutMs": 8000}
    → {"ok": true, "hwnd": 67072, "nodes": 29, "items": [...]}
    → {"ok": false, "error": "..."}

按描述符点击（2.18.31 新增）：
    {"mode": "click", "hwnd": 66988,
     "descriptor": "role=Button/name=首 页", "button": "left", "dryRun": false}
    → {"ok": true, "method": "InvokePattern", "element": {...}}
    → {"ok": true, "method": "Coordinate",   "element": {...}}   ← 由调用方去点这个矩形
    → {"ok": false, "error": "NotFound" | "AmbiguousTarget" | "UnknownOutcome" | ...}

── 为什么点击要绕到这里来 ──

因为 dsc.exe（.NET）在 Chromium 上**读不到元素**：AutomationElement.FromHandle
拿到的是一个 IsControlElement=True、Descendants=0 的"叶子"，FindByDescriptor
永远返回 null，所以 desktop_click_element 在 Electron/Tauri 上稳定报 NotFound。
而同一个窗口用非 .NET 进程读能拿到 39~62 个有名字、有矩形的节点。

点不了不是"读得少"的同一个问题：能读 ≠ 能按名字点。所以点击也必须走这条链。

── 谁执行鼠标动作 ──

InvokePattern 由 PowerShell 自己调（不需要坐标）。
坐标点击**不在这里做**：这里只把元素的物理矩形返回去，由 dsc.exe 去点。
理由是 DPI——PowerShell 默认不是 DPI 感知的，坐标会被系统虚拟化；
dsc.exe 有 Per-Monitor V2 清单，`click` 那条路径是已验证的，
而且它还会检查落点是否真的属于目标窗口。两条链各做自己可靠的那件事。

（末尾那句 "point" 的说明见 click_by_descriptor。）

── 为什么用 PowerShell 而不是 pythoncom ──

因为已验证 PowerShell 能读到 29，而 pythoncom 需要额外装 pywin32。
PowerShell 是 Windows 自带的，不需要任何依赖。
python 在这里只是"非 .NET 的启动器 + JSON 转接"。"""

import json
import os
import subprocess
import sys

# stdout 必须强制 UTF-8。
# 实测踩到：Windows 上的 python 默认用 GBK 编码 stdout，而树里的中文
# （窗口名、按钮文字）会直接抛 UnicodeEncodeError: 'gbk' codec can't encode。
# 表现是桥退出码 1、stdout 空 —— 调用方只能看到"读不到"，看不到真正的原因。
#
# stdin 同样要强制 UTF-8 —— 这一条是 2.18.31 补的，踩得很典型：
# 常驻模式按行读 stdin，而 Windows 上 python 用系统 ANSI（GBK）解码 stdin。
# 调用方发的是 UTF-8，"我 的" 于是被解成 "鎴� 鐨�"，
# 然后这个乱码一路传到 PowerShell，报"未找到匹配元素：role=Button/name=鎴� 鐨�"。
# JSON 结构完全合法、名字只是不对 —— 看起来像"元素不存在"，
# 其实是编码在入口就被解错了。
for _stream in (sys.stdout, sys.stdin):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
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
# ── 统计必须基于**全部**节点，不是列出的那几条 ──
#
# 实测踩到：maxNodes=12 时，前 12 个恰好全是 Pane/Group，
# 于是"带名字 3、可交互 0"——而整棵树其实有 36 个有名字、33 个可交互。
# 结果是插件报"信息很少，建议改用截图"，把一棵能用的树劝退了。
$namedTotal = 0
$actionTotal = 0
$roleCount = @{{}}
foreach ($x in $all) {{
  try {{
    $t = $x.Current.ControlType.ProgrammaticName -replace 'ControlType.','' -replace '^$','Unknown'
    if ($roleCount.ContainsKey($t)) {{ $roleCount[$t] = $roleCount[$t] + 1 }} else {{ $roleCount[$t] = 1 }}
    if ($x.Current.Name -and $x.Current.Name.Trim().Length -gt 0) {{ $namedTotal++ }}
    if ($t -match 'Button|Edit|ComboBox|CheckBox|RadioButton|Hyperlink|ListItem|MenuItem|Tab|TabItem|Slider|TreeItem|DataItem|Text') {{ $actionTotal++ }}
  }} catch {{ }}
}}

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
  named = $namedTotal
  actionable = $actionTotal
  roles = ($roleCount.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {{ $_.Key + '=' + $_.Value }}) -join ', '
  items = $items
}} | ConvertTo-Json -Depth 6 -Compress
"""

# ── 点击脚本 ──
#
# 刻意**不用** .format()：这个脚本里到处是 { }（PowerShell 的哈希表、C# 的
# 方法体），走 format 就得把每一个都写成 {{ }}，改一次错一次。
# 参数一律走环境变量（DSC_HWND / DSC_DESC / ...），既免了转义，
# 也免了"把用户给的描述符拼进 PowerShell 代码"这种注入面。
#
# 匹配规则**必须和 helper 的 FindByDescriptor 一致**（Program.cs）：
#   role  → ControlType.ProgrammaticName 去掉 "ControlType." 前缀
#   name  → Current.Name
#   aid   → Current.AutomationId
#   class → Current.ClassName
# 全部按大小写不敏感做**全等**比较（PowerShell 的 -eq / -ne 对字符串
# 默认就是大小写不敏感，正好对上 OrdinalIgnoreCase）。
#
# 唯一的差别（故意的）：搜索范围是 FindAll(Descendants) 的全部节点 + 候选根，
# 也就是**读树时列出来的那一批**。这样"树里看得到的元素"和"能按名字点的元素"
# 是同一个集合 —— 否则会出现最气人的那种情况：屏幕上明明有这一行，
# 按名字点却说找不到。
CLICK_TEMPLATE = r"""
$ErrorActionPreference = 'Stop'

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# DPI 感知要在碰任何窗口之前声明。
#
# 不声明的后果：进程是"DPI 未感知"，系统把坐标按缩放比虚拟化 ——
# 125% 缩放下读到的矩形只有真实值的 80%，拿它去点会落到别处。
# 而 dsc.exe 有 Per-Monitor V2 清单，两边坐标就对不上了。
# 这里声明成 Per-Monitor V2（-4），让桥给出的矩形就是物理像素。
$dpiMode = 'unset'
try {
  Add-Type -Namespace DscBridge -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'
  if ([DscBridge.Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4))) { $dpiMode = 'per-monitor-v2' }
  else { $dpiMode = 'refused' }
} catch { $dpiMode = 'unavailable' }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class DscBridgeClick {
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@

function Emit($o) {
  Write-Output ($o | ConvertTo-Json -Depth 8 -Compress)
  exit 0
}

$target = [IntPtr][long]$env:DSC_HWND
$desc   = $env:DSC_DESC
$btn    = $env:DSC_BUTTON
$dry    = ($env:DSC_DRY -eq '1')

# ── 解析描述符 ──
$want = @{}
foreach ($part in $desc.Split('/')) {
  $eq = $part.IndexOf('=')
  if ($eq -gt 0) {
    $k = $part.Substring(0, $eq).Trim().ToLower()
    $v = $part.Substring($eq + 1).Trim()
    if ($k.Length -gt 0) { $want[$k] = $v }
  }
}
if ($want.Count -eq 0) {
  Emit @{ ok = $false; error = 'BadDescriptor'; message = '描述符为空，例如 role=Button/name=保存' }
}

# ── 候选窗口：顶层 + Chromium 的内容子窗口 ──
$cands = New-Object System.Collections.ArrayList
[void]$cands.Add($target)
$candNames = New-Object System.Collections.ArrayList
[void]$candNames.Add('(top)')
$cb = [DscBridgeClick+EnumProc]{
  param($hh,$ll)
  $sbn = New-Object System.Text.StringBuilder 256
  [void][DscBridgeClick]::GetClassNameW($hh, $sbn, 256)
  $cls = $sbn.ToString()
  if ($cls -match 'RenderWidgetHost|WRY_WEBVIEW|Chrome_WidgetWin_1|WebView2|Internet Explorer_Server') {
    [void]$script:cands.Add($hh)
    [void]$script:candNames.Add($cls)
  }
  return $true
}
[void][DscBridgeClick]::EnumChildWindows($target, $cb, [IntPtr]::Zero)

# ── 每个候选读一次，按节点数降序 ──
#
# 排序和读树完全一样（谁节点多用谁），这样"刚才树是从哪个窗口读的"
# 和"现在从哪个窗口找元素"是同一个答案。
$scored = New-Object System.Collections.ArrayList
for ($ci = 0; $ci -lt $cands.Count; $ci++) {
  $h = $cands[$ci]
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    if ($el -eq $null) { continue }
    $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                       [System.Windows.Automation.Condition]::TrueCondition)
    if ($all -eq $null -or $all.Count -eq 0) { continue }
    [void]$scored.Add([pscustomobject]@{ h = $h; el = $el; all = $all; n = $all.Count; cls = $candNames[$ci] })
  } catch { }
}
if ($scored.Count -eq 0) {
  Emit @{
    ok = $false
    error = 'NoContentWindow'
    message = '这个窗口没有任何可读的内容子树：顶层和所有 Chromium 内容子窗口都是空的。可能是窗口还没渲染完，或者它根本不是 Chromium 系。'
    dpi = $dpiMode
  }
}
$scored = @($scored | Sort-Object n -Descending)

# ── 按描述符找元素 ──
$hit = $null
$hitCand = $null
$near = New-Object System.Collections.ArrayList
$nearKind = ''
$hasNameKey = $want.ContainsKey('name')
foreach ($cand in $scored) {
  $subjects = New-Object System.Collections.ArrayList
  [void]$subjects.Add($cand.el)
  foreach ($x in $cand.all) { [void]$subjects.Add($x) }

  $hits = New-Object System.Collections.ArrayList
  foreach ($x in $subjects) {
    # 一次读完这个元素的四个属性，同时算两种匹配：
    #   $mAll    —— 全部条件都满足（真命中）
    #   $mNoName —— 除 name 之外都满足（"同类元素"，用来解释为什么没找到）
    # 分两趟扫会再读一遍 Name，而 Chromium 的 Name 是跨进程读，很贵。
    $mAll = $true
    $mNoName = $true
    $nm = ''
    foreach ($k in $want.Keys) {
      $a = $null
      try {
        if     ($k -eq 'role')  { $a = $x.Current.ControlType.ProgrammaticName -replace 'ControlType\.','' }
        elseif ($k -eq 'name')  { $a = $x.Current.Name }
        elseif ($k -eq 'aid')   { $a = $x.Current.AutomationId }
        elseif ($k -eq 'class') { $a = $x.Current.ClassName }
        else { $a = $null }
      } catch { $a = $null }
      if ($k -eq 'name' -and $a -ne $null) { $nm = $a }
      if ($a -eq $null -or $a -ne $want[$k]) {
        $mAll = $false
        if ($k -ne 'name') { $mNoName = $false }
      }
    }
    if ($mAll) { [void]$hits.Add($x) }
    elseif ($mNoName -and $hasNameKey -and $near.Count -lt 8) {
      if ($nm -and -not $near.Contains($nm)) { [void]$near.Add($nm); if ($nearKind -eq '') { $nearKind = 'sameRole' } }
    }
  }

  if ($hits.Count -eq 1) { $hit = $hits[0]; $hitCand = $cand; break }

  if ($hits.Count -gt 1) {
    # 和 helper 一样：多个命中就拒绝，不猜。
    # 点错其中哪一个都不会报错，而且可能做错事。
    $sample = @()
    $i = 0
    foreach ($x in $hits) {
      if ($i -ge 6) { break }
      $tn = ''; $nm = ''
      try { $tn = $x.Current.ControlType.ProgrammaticName -replace 'ControlType\.','' } catch { }
      try { $nm = $x.Current.Name } catch { }
      $sample += ('[' + $i + '] ' + $tn + ' "' + $nm + '"')
      $i++
    }
    Emit @{
      ok = $false
      error = 'AmbiguousTarget'
      message = ('描述符匹配到多个元素（至少 ' + $hits.Count + ' 个）：' + $desc + '。拒绝执行——点错其中哪一个都不会报错，而且可能做错事。请加更具体的条件（例如补上 aid=，或写更完整的 name）。')
      matches = $hits.Count
      sample = $sample
      viaHwnd = [long]$cand.h
    }
  }

  # 0 命中：不再单独扫一遍（near 已经在上面那一趟里顺路攒好了）。
  if ($near.Count -eq 0 -and $nearKind -eq '' -and $cand.n -gt 0) {
    # 连"同类元素"都没有（比如 role 写错了）：退一步给这个窗口里
    # 任意有名字的前几个元素，至少让人看见这里到底有什么。
    foreach ($x in $cand.all) {
      if ($near.Count -ge 8) { break }
      $nm = ''
      try { $nm = $x.Current.Name } catch { }
      if ($nm -and -not $near.Contains($nm)) { [void]$near.Add($nm) }
    }
    if ($near.Count -gt 0) { $nearKind = 'anyNamed' }
  }
}

if ($hit -eq $null) {
  Emit @{
    ok = $false
    error = 'NotFound'
    message = ('未找到匹配元素：' + $desc)
    nearby = @($near)
    nearbyKind = $nearKind
    nodes = [long]$scored[0].n
    viaHwnd = [long]$scored[0].h
    viaClassName = $scored[0].cls
    dpi = $dpiMode
  }
}

# ── 拿到元素了：矩形 + 能不能 Invoke ──
$l = 0; $t = 0; $w = 0; $hh2 = 0
try {
  $b = $hit.Current.BoundingRectangle
  $l = [int]$b.Left; $t = [int]$b.Top; $w = [int]$b.Width; $hh2 = [int]$b.Height
} catch { }
$etype = ''; $ename = ''; $eaid = ''; $ecls = ''
try { $etype = $hit.Current.ControlType.ProgrammaticName -replace 'ControlType\.','' } catch { }
try { $ename = $hit.Current.Name } catch { }
try { $eaid = $hit.Current.AutomationId } catch { }
try { $ecls = $hit.Current.ClassName } catch { }

$ip = $null
$hasInvoke = $false
try { $hasInvoke = $hit.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip) } catch { $hasInvoke = $false }

# ── 动作前最后一次查急停 ──
# 复核（2.18.71）：Python 入口查过一次，但"找元素"可能花几秒，这期间用户按 ESC，
# PowerShell 照样会 Invoke。这里在执行变更动作之前再读一次同一个闸门文件。
# 缺路径、读失败都按"已拉下"处理（fail closed）。
$gate = $env:DSC_HALT_GATE
$haltedNow = $false
try {
  if ([string]::IsNullOrEmpty($gate)) { $haltedNow = $true }
  elseif (Test-Path -LiteralPath $gate) { $haltedNow = $true }
} catch { $haltedNow = $true }

$method = 'Coordinate'
$invokeError = $null
if ($dry) {
  $method = 'DryRun'
} elseif ($haltedNow) {
  $method = 'Halted'
} elseif ($btn -ne 'right' -and $hasInvoke) {
  try {
    $ip.Invoke()
    $method = 'InvokePattern'
  } catch {
    $invokeError = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
  }
}

$elem = @{ type = $etype; name = $ename; aid = $eaid; className = $ecls; l = $l; t = $t; w = $w; h = $hh2 }
$haltedOut = $haltedNow

if ($invokeError -ne $null) {
  # 结果未知：**不重试**，也不假装失败。交给调用方去重新观察。
  # 和 helper 里那条同样的规则：异常不能证明应用没开始执行，
  # 再补一次点击就可能重复触发（点两次提交、发两条消息）。
  Emit @{
    ok = $false
    error = 'UnknownOutcome'
    method = 'InvokePattern'
    element = $elem
    message = ('已调用 InvokePattern 但它抛了异常：' + $invokeError + '。应用可能已经开始执行，也可能没有——异常证明不了是哪一种。不会再去点一次。请重新读树或截图确认界面状态。')
  }
  return
}

# 急停分支必须输出**结构化错误**：复核（2.18.72）发现这里原来算出了 haltedOut，
# 但 Emit 里没有它，于是 Python 收到 ok:true / method:Halted，工具层没法映射成 stop。
if ($haltedNow -and -not $dry) {
  Emit @{
    ok = $false
    error = 'Halted'
    halted = $true
    invoked = $false
    mode = 'click'
    message = ('你在等元素定位的这几秒里按了 ESC —— 动作前复查发现闸门已拉下，' +
               '已中止，没有执行任何点击。不要重试，也不要自行解除闸门。')
  }
  return
}

Emit @{
  ok = $true
  mode = 'click'
  hwnd = [long]$env:DSC_HWND
  viaHwnd = [long]$hitCand.h
  viaClassName = $hitCand.cls
  nodes = [long]$hitCand.n
  matches = 1
  matched = $desc
  method = $method
  invoked = ($method -eq 'InvokePattern')
  hasInvoke = $hasInvoke
  dryRun = $dry
  button = $btn
  dpi = $dpiMode
  element = $elem
}
"""


def _decode(b):
    return (b or b"").decode("utf-8", errors="replace").strip()


def _parse_output(stdout_bytes, stderr_bytes):
    """把 PowerShell 的 stdout 解析成 dict。失败也返回 dict（带 error），不抛。"""
    out = _decode(stdout_bytes)
    if not out:
        err = _decode(stderr_bytes)
        return {"ok": False, "error": "no output", "stderr": err[:500]}
    try:
        # ConvertTo-Json 有时会带 BOM 或者前后空白
        out = out.lstrip("\ufeff").strip()
        return json.loads(out)
    except Exception as e:
        return {"ok": False, "error": "bad json: %s" % e, "raw": out[:500]}


def _run_ps(script, env_extra=None, timeout_ms=8000):
    env = None
    if env_extra:
        env = dict(os.environ)
        env.update(env_extra)
    try:
        r = subprocess.run(
            [PS, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-Command", script],
            capture_output=True, timeout=max(1.0, timeout_ms / 1000.0),
            env=env,
        )
    except subprocess.TimeoutExpired:
        return {"ok": False, "error": "timeout"}
    except Exception as e:
        return {"ok": False, "error": "spawn failed: %s" % e}
    return _parse_output(r.stdout, r.stderr)


def read_tree(hwnd: int, max_nodes: int = 200, timeout_ms: int = 8000) -> dict:
    script = SCRIPT_TEMPLATE.format(hwnd=hwnd, maxNodes=max_nodes)
    return _run_ps(script, None, timeout_ms)


def _halted() -> bool:
    """急停闸门。

    闸门是一个**文件**（路径由插件每次显式传下来，见 toolset 的 DSC_HALT_GATE）。
    文件存在 = 用户按了 ESC 叫停。

    dsc.exe 那条链每一步都查它；桥是另一条链，不查就出现"界面收起来了、
    按钮照样被点"——正是"看起来停了其实没停"。所以这里也查。

    只拦变更性动作（click）。读树（tree）不拦：观察不改变任何东西，
    拦住它只会让人看不见现场。
    """
    gate = os.environ.get("DSC_HALT_GATE", "")
    if not gate:
        return False
    try:
        return os.path.exists(gate)
    except Exception:
        return False


def click_by_descriptor(hwnd: int, descriptor: str, button: str = "left",
                        dry_run: bool = False, timeout_ms: int = 12000) -> dict:
    """按描述符找元素，能 Invoke 就 Invoke，否则只返回矩形交给调用方去点。

    dry_run=True 时只找、不动：method 变成 'DryRun'，用来验证匹配是否成立
    （测试用，不对外暴露成工具）。
    """
    if _halted():
        return {
            "ok": False,
            "mode": "click",
            "error": "Halted",
            "message": ("用户按了 ESC 叫停。桥这一侧同样拒绝执行变更性动作。"
                        "不要重试，也不要自行解除闸门——它由人来清。"),
        }
    env = {
        "DSC_HWND": str(int(hwnd)),
        "DSC_DESC": descriptor or "",
        "DSC_BUTTON": "right" if (button or "").lower() == "right" else "left",
        "DSC_DRY": "1" if dry_run else "0",
        # 动作前复查要读同一个闸门；路径由 toolset 每次显式传下来
        "DSC_HALT_GATE": os.environ.get("DSC_HALT_GATE", ""),
    }
    res = _run_ps(CLICK_TEMPLATE, env, timeout_ms)
    if not isinstance(res, dict):
        return {"ok": False, "error": "bad result", "raw": str(res)[:300]}
    # PowerShell 在动作前复查了闸门：读到这里就说明"找到元素之后被急停"。
    # 这时**不能**继续，也不能把 method 当成正常点击报给调用方。
    if res.get("haltedOut") or res.get("halted"):
        return {
            "ok": False,
            "mode": "click",
            "error": "Halted",
            "message": ("你在等元素定位的这几秒里按了 ESC —— 桥在真正 Invoke 之前"
                        "复查了闸门，已中止。没有执行任何点击。不要重试，也不要自行解除闸门。"),
        }
    # ConvertTo-Json 会把只有一个元素的数组压成对象，统一回数组。
    for k in ("nearby", "sample"):
        v = res.get(k)
        if v is not None and not isinstance(v, list):
            res[k] = [v]
        elif v is None:
            res[k] = []
    res.setdefault("mode", "click")
    return res


def handle(req: dict) -> dict:
    mode = str(req.get("mode") or "tree").strip().lower()
    if mode == "click":
        return click_by_descriptor(
            int(req.get("hwnd", 0) or 0),
            str(req.get("descriptor") or ""),
            str(req.get("button") or "left"),
            bool(req.get("dryRun", False)),
            int(req.get("timeoutMs", 12000) or 12000),
        )
    return read_tree(
        int(req.get("hwnd", 0) or 0),
        int(req.get("maxNodes", 200) or 200),
        int(req.get("timeoutMs", 8000) or 8000),
    )


def main():
    # 单次模式
    if len(sys.argv) > 1:
        if sys.argv[1] == "--click":
            try:
                req = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
            except Exception as e:
                sys.stdout.write(json.dumps(
                    {"ok": False, "error": "bad request json: %s" % e}) + "\n")
                return 2
            req["mode"] = "click"
            result = handle(req)
        else:
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
        try:
            result = handle(req)
        except Exception as e:
            result = {"ok": False, "error": "handler failed: %s" % e}
        sys.stdout.write(json.dumps(result, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
