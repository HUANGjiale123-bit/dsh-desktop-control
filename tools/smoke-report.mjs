#!/usr/bin/env node
/* 在**别人的机器**上跑一遍，输出一段可以整段贴回来的体检报告。
 *
 * 为什么不在文档里写"请检查以下十项"：那样人只会做前三项，然后凭感觉说"没问题"。
 * 这个脚本把**能自动采的**全部采完（环境、权限、DPI、OCR、光标素材、闸门状态），
 * 剩下真正需要人眼判断的（点击准不准、光标对不对齐）列成清单。
 *
 * 用法：
 *   node tools/smoke-report.mjs
 *
 * 只读：不改光标、不点击、不启动任何东西（光标彩蛋那段只读状态，不开启）。
 */

import { spawnSync } from 'node:child_process'
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { homedir } from 'node:os'

const rows = []
const add = (section, item, value, note = '') => rows.push({ section, item, value, note })
const run = (cmd, args, opts = {}) =>
  spawnSync(cmd, args, { encoding: 'utf8', windowsHide: true, timeout: 20000, ...opts })

// ── 找到已安装的插件（优先 profile 里的那一份，那才是真正在跑的）──
const profileRoot = join(homedir(), '.dsh', 'profiles')
let pluginDir = null
let profileName = null
try {
  for (const name of readdirSync(profileRoot)) {
    const p = join(profileRoot, name, 'node_modules', 'dsh-desktop-control')
    if (existsSync(join(p, 'package.json'))) { pluginDir = p; profileName = name; break }
  }
} catch { }

const scope = pluginDir ? `profile:${profileName}` : '（未在 profile 里找到）'
add('插件', '安装位置', pluginDir ?? '未找到', profileDirNote())

function profileDirNote() {
  return pluginDir ? '' : '插件可能没装，或装在了别的 profile 下'
}

// ── 版本 ──
if (pluginDir) {
  try {
    const pkg = JSON.parse(readFileSync(join(pluginDir, 'package.json'), 'utf8'))
    add('插件', '版本', pkg.version)
    add('插件', '声明许可', pkg.license ?? '（未声明）')
  } catch { add('插件', '版本', '读不到 package.json') }
}

// ── helper ping：DPI 感知、完整性级别、闸门、系统版本 ──
const exe = pluginDir ? join(pluginDir, 'bin', 'dsc.exe') : null
if (exe && existsSync(exe)) {
  const r = run(exe, ['ping'])
  try {
    const j = JSON.parse(r.stdout)
    add('helper', 'DPI 感知', `${j.dpiAwareness}（dpiAware=${j.dpiAware}）`)
    add('helper', '完整性级别', j.integrity,
      j.integrity === 'Low' ? '⚠ Low 会让鼠标/键盘注入静默失效，要用 Medium' : '')
    add('helper', '系统', j.os)
    // 闸门路径与状态
    add('helper', '闸门', j.halted ? '⚠ 正在急停' : '未拉下')
  } catch {
    add('helper', 'ping', `失败：${(r.stdout || r.stderr || '').slice(0, 80)}`)
  }
} else {
  add('helper', 'dsc.exe', exe ? '不存在' : '（没找到插件目录）')
}

// ── 屏幕与 DPI ──
const ps = `
Add-Type -Namespace S -Name M -MemberDefinition @'
[DllImport("user32.dll")] public static extern void SetProcessDpiAwarenessContext(IntPtr v);
[DllImport("user32.dll")] public static extern uint GetDpiForSystem();
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int i, uint dpi);
'@
[void][S.M]::SetProcessDpiAwarenessContext([IntPtr](-4))
$dpi = [S.M]::GetDpiForSystem()
[Console]::WriteLine("DPI_D=" + $dpi)
[Console]::WriteLine("SW=" + [S.M]::GetSystemMetrics(0) + "x" + [S.M]::GetSystemMetrics(1))
[Console]::WriteLine("CURSOR=" + [S.M]::GetSystemMetricsForDpi(13, $dpi))
`
const r = run('powershell', ['-NoProfile', '-Command', ps])
const out = String(r.stdout || '')
const grab = (k) => (out.match(new RegExp(k + '=([^\\r\\n]+)')) || [])[1]?.trim() ?? '?'
add('环境', '系统 DPI', `${grab('DPI_D')}（${grab('DPI_D') === '?' ? '?' : Math.round(Number(grab('DPI_D')) / 96 * 100) + '%'}）`)
add('环境', '桌面分辨率', grab('SW'))
add('环境', '光标尺寸', `${grab('CURSOR')}px`)

// ── 显示器数量（用 WMI，不依赖 P/Invoke 委托）──
const mon = run('powershell', ['-NoProfile', '-Command',
  '(Get-CimInstance Win32_VideoController | Measure-Object).Count; ' +
  '(Get-CimInstance Win32_VideoController | Select-Object -First 1 -ExpandProperty CurrentHorizontalResolution)'])
const monLines = String(mon.stdout || '').trim().split('\n').map((s) => s.trim())
if (monLines.length >= 2) add('环境', '显示适配器数', monLines[0])

// ── OCR 语言 ──
const ocr = pluginDir ? join(pluginDir, 'win-ocr.ps1') : null
if (ocr && existsSync(ocr)) {
  const r2 = run('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ocr, '-ProbeOnly'])
  try {
    const j = JSON.parse(r2.stdout)
    add('OCR', '后端', j.backend ?? '?')
    add('OCR', '语言包', (j.languages ?? []).join(', ') || '（无）',
      (j.languages ?? []).length ? '' : '⚠ 没有 OCR 语言包，视觉兜底不可用')
  } catch {
    add('OCR', '探测', `失败：${(r2.stdout || r2.stderr || '').slice(0, 80)}`)
  }
}

// ── 光标素材与运行状态 ──
if (pluginDir) {
  const arrow = join(pluginDir, 'assets', 'cursor', 'poses-arrow')
  const n = existsSync(arrow) ? readdirSync(arrow).filter((f) => f.endsWith('.png')).length : 0
  add('光标', '透明光标素材', `${n} 张`, n === 10 ? '' : '⚠ 应为 10 张，包可能不完整')

  const fxDir = join(process.env.TEMP ?? '', 'dsc-cursor-fx')
  add('光标', '运行状态', existsSync(join(fxDir, 'fx.blanked')) ? '正在拦截光标' : '未启用')
  const marker = join(process.env.TEMP ?? '', 'dsc-cursor-fx.enable')
  add('光标', '开关文件', existsSync(marker) ? `存在（内容 ${JSON.stringify(readFileSync(marker, 'utf8').trim())}）` : '不存在（默认关闭）')
}

// ── 输出成一段 markdown ──
const pad = (s, n) => String(s).padEnd(n)
console.log('```')
console.log(`# dsh-desktop-control 体检报告  ${new Date().toISOString().slice(0, 19).replace('T', ' ')}`)
console.log(`# ${scope}`)
console.log()
let section = ''
for (const r of rows) {
  if (r.section !== section) { section = r.section; console.log(`\n[${section}]`) }
  console.log(`  ${pad(r.item, 12)} ${r.value}${r.note ? '   ' + r.note : ''}`)
}
console.log('```')
console.log()
console.log('（这段整段贴回来即可。人眼要判断的项见 docs/第二台机器验收清单.md）')
