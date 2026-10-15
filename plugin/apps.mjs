// 已安装应用清单 —— 从注册表枚举，**不经过 helper**。
//
// ── 为什么要自己实现，而不是调用 helper 的 list_apps / resolve_app ──
//
// 因为**启动这个动作不能交给沙箱内的 helper**。实测（2026-10-15）：
//
//   传统安装的应用（腾讯视频 D:\QQLive）经 helper 启动 → 存活，有窗口
//   Store/MSIX 应用（记事本、画图）经 helper 启动      → 约 4 秒后自己退出
//
// 事件日志给出原因方向：ucrtbase.dll 里 __fastfail（异常码 0xc0000409）。
// 打包应用启动时要访问自己的包数据目录（%LOCALAPPDATA%\Packages\...），
// 而 helper 是 DSH 的**沙箱子进程**（写受限 token + 能力 SID ACL），
// 访问失败就自我终止。同一台机器上从沙箱外启动同一个 exe，全程正常。
//
// 所以启动必须由插件自己做——插件跑在 DSH 宿主里，不在沙箱内。
// 而"启动"需要"先解析出 exe"，解析又必须和启动**在同一个信任边界内**，
// 否则路径对不上（同一套判定在两边实现，迟早分叉）。因此这里把注册表
// 枚举一并搬过来，helper 的 launch/list_apps 保留给命令行用法。
//
// ── 枚举范围 ──
//
// 只取注册表里**声明了显示名**的卸载项，加上 App Paths。不扫开始菜单
// 快捷方式：那要解析 .lnk，还会把卸载入口和帮助文档算成应用。
//
// 判定为"不能确定主程序"时**宁可跳过**——启动错的程序比少列一个严重。

import { existsSync, readdirSync, statSync } from 'node:fs'
import { join, basename, extname } from 'node:path'
import { execFileSync } from 'node:child_process'

/**
 * 读一个注册表根键下的全部子键与值，输出 UTF-8 JSON。
 *
 * ── 为什么不用 reg.exe ──
 *
 * 一开始用 `reg query`，结果**中文全变乱码**、清单从 80 个缩到 51 个：
 * reg.exe 按**系统 OEM 代码页**输出（中文系统是 GBK），而 Node 按 UTF-8 解，
 * 于是所有非 ASCII 的显示名被破坏 —— 名字坏了，按名字搜索自然也就找不到
 * （实测"腾讯"直接搜不到）。这不是显示问题，是**数据丢失**。
 *
 * 所以改成让 PowerShell 读注册表并显式输出 UTF-8：编码在源头就定死，
 * 不依赖系统代码页。慢一点（每次起一个 PowerShell），但清单本身是缓存的，
 * 只在第一次调用时付这个代价。
 */
function readRegistryRoot(root) {
  const script = `
$ErrorActionPreference = 'SilentlyContinue'
$out = [System.Collections.Generic.List[object]]::new()
$root = '${root.replace(/'/g, "''")}'
$base = Get-Item -LiteralPath ("Registry::" + $root)
if ($base -ne $null) {
  foreach ($sub in $base.GetSubKeyNames()) {
    $k = $base.OpenSubKey($sub)
    if ($k -eq $null) { continue }
    $vals = @{}
    foreach ($n in $k.GetValueNames()) {
      $v = $k.GetValue($n)
      if ($v -is [array]) { $v = ($v -join ' ') }
      $vals[$n] = [string]$v
    }
    $out.Add([pscustomobject]@{ key = $sub; values = $vals })
    $k.Close()
  }
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
ConvertTo-Json -InputObject @($out) -Depth 4 -Compress
`
  try {
    const buf = execFileSync(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script],
      { windowsHide: true, maxBuffer: 64 * 1024 * 1024 },
    )
    const text = new TextDecoder('utf-8', { fatal: false }).decode(buf).trim()
    if (!text) return []
    const parsed = JSON.parse(text)
    const arr = Array.isArray(parsed) ? parsed : [parsed]
    return arr
      .filter((x) => x && typeof x.key === 'string')
      .map((x) => ({ key: x.key, values: x.values ?? {} }))
  } catch {
    return []
  }
}

const UNINSTALL_ROOTS = [
  'HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
  'HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
  'HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
]
const APPPATH_ROOTS = [
  'HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths',
  'HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\App Paths',
  'HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths',
]

/**
 * 在一个目录里挑最像主程序的 exe。
 *
 * 规则刻意保守：优先与目录同名的；否则只有唯一一个**合格**候选时才取。
 * 多个合格候选且没有同名时返回空——**宁可少列一个，也不要指错程序**。
 *
 * ★ 过滤卸载/安装/更新类程序，**单候选时也要过滤**。
 *
 * 踩到的：剪映专业版的目录里唯一那个 exe 恰好是 `uninst.exe`，
 * 于是清单里"剪映专业版"指向了它的**卸载程序**——照它启动会去卸载软件。
 * 原先的过滤只在"多候选"那条分支里，单候选直接返回，把最危险的那种放过去了。
 */
function pickExe(dir) {
  try {
    const exes = readdirSync(dir).filter((f) => f.toLowerCase().endsWith('.exe'))
    if (exes.length === 0) return ''

    // 先滤掉明显不是主程序的（卸载器、安装器、更新器、崩溃上报…）
    const plausible = exes.filter((f) => !looksLikeHelper(f))
    if (plausible.length === 0) return ''

    const dirName = basename(dir.replace(/[\\/]+$/, '')).toLowerCase()
    // 与目录同名优先——但同样只在"合格"的范围里挑
    for (const f of plausible) {
      if (basename(f, extname(f)).toLowerCase() === dirName) return join(dir, f)
    }
    if (plausible.length === 1) return join(dir, plausible[0])
    return ''
  } catch {
    return ''
  }
}

/**
 * 这个 exe 看起来是"辅助程序"而不是主程序吗？
 *
 * 卸载器、安装器、更新器、崩溃上报——**照它们启动会做危险的事**，
 * 最坏的情况是真去卸载软件（实测清单里出现过 `Steam → uninstall.exe`、
 * `剪映专业版 → uninst.exe`）。
 *
 * 做成一道统一的判定，**所有来源都要过**（DisplayIcon / InstallLocation /
 * UninstallString / App Paths）。早先只在 pickExe 的一个分支里过滤，
 * 于是 DisplayIcon 那条优先级最高的路完全没被约束。
 *
 * 注意 `install` 也在列表里：`OneDriveSetup.exe` 这类必须挡住。
 * 但 `wzmain.exe`（WinZips 的主程序）不含这些词，正常通过。
 */
function looksLikeHelper(exe) {
  const n = basename(exe, extname(exe)).toLowerCase()
  return /uninst|setup|install|update|upgrade|crash|report|repair|helper/.test(n)
}

/** 从卸载项里找可执行文件。三条线索，按可靠性排序；取不到就返回空。 */
function findExe(values, keyName) {
  // ① DisplayIcon —— 最可靠，形如 "C:\...\app.exe,0"
  const icon = values['DisplayIcon']
  if (icon) {
    let p = icon.trim().replace(/^"|"$/g, '')
    const comma = p.lastIndexOf(',')
    if (comma > 2) p = p.slice(0, comma)
    p = p.trim().replace(/^"|"$/g, '')
    if (/\.exe$/i.test(p) && existsSync(p) && !looksLikeHelper(p)) return p
  }
  // ② InstallLocation
  const loc = values['InstallLocation']
  if (loc) {
    const dir = loc.trim().replace(/^"|"$/g, '').replace(/[\\/]+$/, '')
    if (dir && existsSync(dir)) {
      const hit = pickExe(dir)
      if (hit && !looksLikeHelper(hit)) return hit
    }
  }
  // ③ UninstallString 所在目录
  const us = values['UninstallString']
  if (us) {
    let p = us.trim().replace(/^"|"$/g, '')
    const sp = p.indexOf(' -')
    if (sp > 0) p = p.slice(0, sp).trim().replace(/^"|"$/g, '')
    const dir = p.replace(/[\\/][^\\/]*$/, '')
    if (dir && existsSync(dir)) {
      const hit = pickExe(dir)
      if (hit && !looksLikeHelper(hit)) return hit
    }
  }
  return ''
}

let _cache = null

/** 已安装应用清单（首次调用时枚举并缓存）。 */
export function listInstalledApps() {
  if (_cache) return _cache
  const list = []
  const seen = new Set()

  for (const root of UNINSTALL_ROOTS) {
    for (const { key, values } of readRegistryRoot(root)) {
      const name = (values['DisplayName'] ?? '').trim()
      if (!name) continue
      // 系统组件与更新补丁不算"应用"
      if (String(values['SystemComponent'] ?? '') === '1') continue
      if (values['ParentKeyName'] !== undefined) continue

      const exe = findExe(values) || findExe(values, key)
      if (!exe) continue
      const k = exe.toLowerCase()
      if (seen.has(k)) continue
      seen.add(k)
      list.push({ name, exe, version: (values['DisplayVersion'] ?? '').trim(), source: 'uninstall' })
    }
  }

  for (const root of APPPATH_ROOTS) {
    for (const { key, values } of readRegistryRoot(root)) {
      const exe = (values[''] ?? '').trim().replace(/^"|"$/g, '')
      if (!exe || !existsSync(exe)) continue
      if (looksLikeHelper(exe)) continue
      const k = exe.toLowerCase()
      if (seen.has(k)) continue
      seen.add(k)
      const sub = key.split('\\').pop() ?? ''
      list.push({
        name: basename(sub, extname(sub)),
        exe,
        version: '',
        source: 'apppath',
      })
    }
  }

  list.sort((a, b) => a.name.localeCompare(b.name, 'zh'))
  _cache = list
  return list
}

/** 按关键词筛选，用于"没匹配上时给候选"。 */
export function searchApps(query, max = 8) {
  const q = String(query ?? '').toLowerCase()
  if (!q) return []
  const out = []
  for (const a of listInstalledApps()) {
    if (a.name.toLowerCase().includes(q) || basename(a.exe).toLowerCase().includes(q)) {
      out.push(a)
      if (out.length >= max) break
    }
  }
  return out
}

/**
 * 把用户给的名字解析成一个确定的可执行文件。
 *
 * 返回 `{ ok: true, app }` 或 `{ ok: false, reason: 'NoMatch' | 'Ambiguous', candidates }`。
 *
 * 只接受两种目标：清单里的名字，或明确的 .exe 完整路径。**不接受任意命令行**——
 * 否则这个接口等同于"以用户权限执行任意程序"。
 */
export function resolveApp(query) {
  const q = String(query ?? '').trim()
  if (!q) return { ok: false, reason: 'NoMatch', candidates: [] }

  // ① 明确的 .exe 路径
  if (/\.exe$/i.test(q) && /[\\/]/.test(q)) {
    return existsSync(q)
      ? { ok: true, app: { name: basename(q, extname(q)), exe: q, version: '' } }
      : { ok: false, reason: 'NoMatch', candidates: [] }
  }

  const all = listInstalledApps()

  // ② 精确匹配显示名
  for (const a of all) if (a.name.toLowerCase() === q.toLowerCase()) return { ok: true, app: a }

  // ③ 匹配 exe 文件名
  const qFile = /\.exe$/i.test(q) ? q.toLowerCase() : q.toLowerCase() + '.exe'
  for (const a of all) if (basename(a.exe).toLowerCase() === qFile) return { ok: true, app: a }

  // ④ 唯一命中才算
  const hits = all.filter((a) => a.name.toLowerCase().includes(q.toLowerCase()))
  if (hits.length === 1) return { ok: true, app: hits[0] }
  if (hits.length === 0) return { ok: false, reason: 'NoMatch', candidates: [] }
  return { ok: false, reason: 'Ambiguous', candidates: hits.slice(0, 8) }
}
