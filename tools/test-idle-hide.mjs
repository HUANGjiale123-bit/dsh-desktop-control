// 自动拉起的指示层必须"空闲后自己收"—— 回归测试。
//
// 用户的反馈原话："完成命令后不会自动退出，体验很不好。"
// 最初实现是"拉起后 2 分钟自动收"，两个方向都不对：
//   · 连续操作超过 2 分钟 → 中途消失
//   · 操作 5 秒就结束     → 还挂 2 分钟
// 所以改成滑动窗口：**距上一次操作超过 IDLE_HIDE_MS 才收**。
//
// 这个测试真的会拉起 dsc-esc（屏幕上会短暂出现光带与逃生条），
// 然后在空闲后观察它是否被杀掉、以及**闸门有没有被误写**。
//
// 为了不等 20 秒，这里把源码里的 IDLE_HIDE_MS 临时替换成 4 秒再加载
// —— 测的是同一段逻辑，只是把时间轴压缩。
//
// 用法：node tools/test-idle-hide.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync, existsSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const HELPER_DIR = process.argv[2] ?? 'helper/bin'
const DSC = join(HELPER_DIR, 'dsc.exe')
const ESC = join(HELPER_DIR, 'dsc-esc.exe')

// 闸门必须在工作区内（沙箱子进程只能写那里）。用一个临时目录，
// 且**不用真实闸门**——这个测试不该触发急停。
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'idle-test-'))
const GATE = join(tmp, 'halt')

console.log()
console.log(`  闸门（临时）：${GATE}`)
console.log('  （测试会真的拉起 dsc-esc，屏幕上会短暂出现光带）')

// ── 构造插件副本 ──
//
// 原先这里改源码（`IDLE_HIDE_MS = 20000` → 4000），因为当时它是**硬编码**的。
// 后来它变成了可配置（config.idleHideMs，默认 8000），这个替换就一直失败 ——
// 而测试没人跑，所以坏了很久也没发现。
//
// 现在不再改源码：直接传 config.idleHideMs = 4000。
// 这样源码怎么演化都不会让测试失效，而且测的是**真实路径**而不是打补丁后的。
const srcPath = 'plugin/index.mjs'
const src = readFileSync(srcPath, 'utf8')
const patched = join(tmp, 'index.mjs')
writeFileSync(patched, src)

// 插件副本要能解析到同目录的 toolset.mjs / tooldsl.mjs，所以复制过去
for (const f of ['toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs']) {
  try {
    writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
  } catch { }
}

// ── stub ctx ──
const registered = []
const logs = []
const children = []
const ctx = {
  logger: {
    info: (m) => logs.push(['info', String(m)]),
    warn: (m) => logs.push(['warn', String(m)]),
    error: (m) => logs.push(['error', String(m)]),
  },
  tools: {
    register: (t) => {
      registered.push(t)
      return () => {}
    },
  },
  get(name) {
    if (name === 'approval') return { request: async () => 'allowed-once' }
    if (name === 'agents') return { currentInitiator: () => ({ id: 'session-test' }) }
    if (name === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['session-test'] }] }
    return undefined
  },
}

let plugin = null
try {
  plugin = await import(pathToFileURL(patched).href)
} catch (e) {
  console.log(`  ✗ 加载补丁版插件失败：${e.message}`)
  process.exit(1)
}

try {
  await plugin.apply(ctx, { helperPath: DSC, escPath: ESC, gatePath: GATE, idleHideMs: 4000 })
} catch (e) {
  console.log(`  ✗ apply 失败：${e.message}`)
  process.exit(1)
}

const byName = new Map(registered.map((t) => [t.name, t]))
console.log()
console.log(`  已注册 ${registered.length} 个工具`)

// ── 触发一次变更性操作（会走授权 → onAuthorized → ensureIndicator）──
// 用一个几乎肯定失败的 click（坐标在窗口外）不行——那会在授权前返回。
// 所以直接用 desktop_indicate？不行，那条不走 ensureIndicator。
// 用 desktop_screenshot：走授权，且不需要坐标。
// 但它需要真实 hwnd。拿一个来。
let hwnd = 0
try {
  const r = spawnSync(DSC, ['list_windows'], { encoding: 'utf8', windowsHide: true })
  const w = JSON.parse(r.stdout).windows.find((x) => x.width > 300 && x.exe)
  if (w) hwnd = w.hwnd
} catch { }

if (!hwnd) {
  console.log('  ✗ 拿不到窗口句柄，无法触发操作')
  process.exit(1)
}

const shotDir = join(tmp, 'shots')
await plugin.apply === undefined ? null : null
// 重新 apply 一份带 shotDir 的（config 已定，这里直接用 screenshot 默认目录即可）
const shot = byName.get('desktop_screenshot')
if (!shot) {
  console.log('  ✗ 找不到 desktop_screenshot')
  process.exit(1)
}

const op = await shot.execute({ hwnd, maxNodes: 5 }).catch((e) => ({ error: e.message }))
console.log(`  触发一次操作（screenshot hwnd=${hwnd}）→ ${op.error ? '失败: ' + op.error : '成功'}`)

// ── 观察：指示层应当被拉起 ──
//
// 两件不同的事，用两个不同的信号：
//
//   · "**有没有**指示层在跑"  → 看系统里有没有 dsc-esc 进程（任意一个）。
//     这正是用户看到的东西，所以"拉起"和"持续期间不收"用它。
//   · "**这次插件实例有没有收起它**" → 看**日志里那条"自动收起指示层"**。
//     不能用"进程消失"来判断：dsc-esc 是单实例的，如果用户自己的 ESC 监听器
//     本来就在跑，这个测试就永远 spawn 不出自己的进程，于是"空闲后应自动收起"
//     会在整套测试连跑时假红、单跑时又假绿。
function escPids() {
  const out = spawnSync('powershell.exe', ['-NoProfile', '-Command',
    "(Get-Process dsc-esc -EA SilentlyContinue | ForEach-Object { $_.Id }) -join ','"],
    { encoding: 'utf8', windowsHide: true }).stdout.trim()
  return out.split(',').map((s) => s.trim()).filter(Boolean)
}

const preexistingEsc = new Set(escPids())
const anyEsc = () => escPids().length > 0
const ourEsc = () => escPids().filter((id) => !preexistingEsc.has(id))
const hidByUs = () => logs.some((l) => l[1].includes('自动收起指示层'))

if (preexistingEsc.size > 0) {
  console.log(`  · 开跑前已有 ${preexistingEsc.size} 个 dsc-esc 在跑（你自己的 ESC 监听器？）`
    + '——本次不再拉起新实例，收起改用日志判断')
}

await new Promise((r) => setTimeout(r, 1500))
if (anyEsc()) ok('变更性操作后指示层被自动拉起')
else {
  bad('指示层没有被拉起 —— 自动显示能力坏了')
  console.log('     相关日志:', logs.slice(-3).map((l) => l[1]).join(' | '))
}

// ── 继续操作一次，确认计时被推后（此时不该收）──
await new Promise((r) => setTimeout(r, 2000))
await shot.execute({ hwnd, maxNodes: 5 }).catch(() => { })
await new Promise((r) => setTimeout(r, 2500))
if (anyEsc()) ok('操作持续期间不会被收（锚点被推后）')
else bad('操作还在进行时指示层就被收了 —— 计时没重置')

// ── 停止操作，等超过 IDLE_HIDE_MS（4 秒）+ 检查间隔 ──
await new Promise((r) => setTimeout(r, 9000))
if (hidByUs()) ok('空闲后自动收起（日志里有收起记录）')
else if (ourEsc().length === 0 && preexistingEsc.size > 0) {
  bad('空闲 9 秒后仍没有收起记录 —— 插件没有收掉自己该收的指示层')
} else if (ourEsc().length === 0) {
  bad('空闲 9 秒后仍没有收起记录（进程也没了，但日志没说为什么）')
} else {
  bad('空闲 9 秒后指示层仍在（IDLE_HIDE_MS=4 秒，应该早收了）')
}

// ── 关键：收起不能写闸门 ──
if (!existsSync(GATE)) ok('自动收起**没有**写闸门（收界面 ≠ 停止）')
else bad('自动收起写了闸门 —— 用户没按停止却被锁住')

// 日志里应当有那条说明
const hideLog = logs.find((l) => l[1].includes('自动收起指示层'))
if (hideLog) ok(`日志记录了收起原因：${hideLog[1].slice(0, 60)}`)
else bad('日志里没有收起记录 —— 事后无法判断它为什么消失')

// 清理：只收掉**本次测试拉起的** dsc-esc。
// 用户自己的 ESC 监听器（开跑前就在的那些）一律不动 —— 那是急停能力，不是垃圾。
try {
  const mine = escPids().filter((id) => !preexistingEsc.has(id))
  if (mine.length) {
    spawnSync('powershell.exe', ['-NoProfile', '-Command',
      `Stop-Process -Id ${mine.join(',')} -Force -ErrorAction SilentlyContinue`],
      { windowsHide: true })
  }
} catch { /* 收不掉就留给系统 */ }
try { rmSync(tmp, { recursive: true, force: true }) } catch { }

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
