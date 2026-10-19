#!/usr/bin/env node
/* A 项：多条件验证矩阵 —— 窗口移动 / 改大小 / 被遮挡 / 内容变了 / 过期，
 * 以及"旧 shotId 在这些情况下到底会不会被拦住"。
 *
 * 为什么单独写一个：这条链有三层校验，各自的判据不同，任何一层松一点，
 * 表现都是"点击照常发出、照常报成功，但落点已经不是原来那个东西"——
 * 那是本项目里最贵的一类错误（失败不报错）。
 *
 * 三层：
 *   ① index 层 checkShot   记录存在 / hwnd 一致 / 有几何 / 未过期
 *   ② toolset 层 click     尺寸变了拒绝；位置变了看坐标空间（desktop 拒绝、window 放行+警告）
 *   ③ helper 层 click      WindowFromPoint 判落点属于谁，不属于目标就拒绝
 *
 * 这个测试**真的移动/缩放/遮挡用户的窗口**，跑完会在 finally 里复原。
 * 靶子优先选 Tauri 窗口（SuYou），因为它不是 DSH 自己，动了不影响正在看的界面。
 *
 * 用法：node tools/test-shot-matrix.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, rmSync, existsSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
// 跳过**不计入通过**：没被验证的断言算成绿色，比失败更误导。
let skipped = 0
const skip = (m) => { console.log(`  · ${m}（未验证）`); skipped++ }

const PS = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe'

function ps(script) {
  const r = spawnSync(PS, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-Command', script], { encoding: 'utf8', windowsHide: true })
  return { status: r.status, out: String(r.stdout ?? '').trim(), err: String(r.stderr ?? '').trim() }
}

/* ── 窗口几何操作 ──
 *
 * ★ 必须先声明 DPI 感知，再调 SetWindowPos。
 *
 * 踩过：这台机器是 150% 缩放，而 powershell.exe 默认是"DPI 未感知"进程，
 * 于是 SetWindowPos(…, 688, 1055, …) 被系统按 1.5 倍放大成 1033x1583 ——
 * 窗口被撑大、位置也偏了，而那个测试格子报的是"尺寸变了"（校验本身没错，
 * 错的是我给出的尺寸）。跟桥那边踩的是**同一个**坑：不声明感知，坐标就不可信。
 */
const PS_PRELUDE =
  `Add-Type -Namespace W -Name D -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'; ` +
  `[void][W.D]::SetProcessDpiAwarenessContext([IntPtr](-4)); ` +
  `Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr z, int x, int y, int cx, int cy, uint f);'; `

const SWP_NOZORDER_NOACT = '0x0004 -bor 0x0010'
const SWP_TOPMOST = '0x0001 -bor 0x0040 -bor 0x0010' // HWND_TOPMOST | SHOWWINDOW | NOACTIVATE

/* ★ 不能传 windowsHide / detached。
 *
 * 实测：`windowsHide: true` 会通过 STARTUPINFO 的 wShowWindow 把**进程的第一个
 * 窗口**也弄成隐藏，哪怕后面自己调 ShowWindow(SW_SHOWNORMAL) 也救不回来；
 * `detached: true` 同样起不来。表现是"窗口创建成功、Visible=True，
 * 但 WindowFromPoint 在屏幕上找不到它"——查了两轮才定位到这里。
 * 代价：跑这一格时屏幕上会闪一下控制台窗口。
 */
function spawnDetached(script) {
  try {
    return spawn(PS, ['-NoProfile', '-NonInteractive',
      '-ExecutionPolicy', 'Bypass', '-Command', script],
      { stdio: ['ignore', 'ignore', 'ignore'] })
  } catch {
    return null
  }
}

/* "这一点现在属于哪个顶层窗口" —— 和 helper 判定落点用的是同一个 API。
 *
 * 为什么不用 list_windows 去发现遮挡窗口：实测那个窗体根本不出现在列表里
 * （ShowInTaskbar=$false 会被当成工具窗口过滤掉），于是"等它出现"永远等不到。
 * 直接问 WindowFromPoint 更接近 helper 的真实判据，也不依赖列表的过滤规则。
 */
const ROOT_AT_TYPE = [
  'using System;',
  'using System.Runtime.InteropServices;',
  'public class DscQ {',
  '  public struct POINT { public int X; public int Y; }',
  '  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);',
  '  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);',
  '  public static long RootAt(int x, int y) {',
  '    POINT p; p.X = x; p.Y = y;',
  '    IntPtr h = WindowFromPoint(p);',
  '    if (h == IntPtr.Zero) return 0;',
  '    return GetAncestor(h, 2).ToInt64();',
  '  }',
  '}',
].join('\n')

function rootAt(px, py) {
  const script = [PS_PRELUDE.trim(),
    `Add-Type -TypeDefinition @'\n${ROOT_AT_TYPE}\n'@`,
    `[DscQ]::RootAt(${px}, ${py})`].join('\n')
  const r = ps(script)
  const n = Number(String(r.out).split('\n').pop())
  return Number.isFinite(n) ? n : 0
}

function win32Ready() {
  const r = ps(`${PS_PRELUDE}'ok'`)
  return r.status === 0 && r.out.includes('ok')
}

function setWindowPos(hwnd, x, y, w, h, flags = SWP_NOZORDER_NOACT) {
  const r = ps(
    `${PS_PRELUDE}[W.U]::SetWindowPos([IntPtr]${hwnd}, [IntPtr]::Zero, ${x}, ${y}, ${w}, ${h}, ${flags})`)
  return r.out.includes('True')
}

/* ── 把插件层装起来（和 test-shot-invalidate 用的是同一套桩） ── */

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'shotmatrix-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

const reg = []
const ctx = {
  logger: { info: () => {}, warn: () => {}, error: () => {} },
  tools: { register: (t) => { reg.push(t); return () => {} } },
  get(n) {
    if (n === 'approval') return { request: async () => 'allowed-once' }
    if (n === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
    if (n === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['s1'] }] }
    if (n === 'attachments') return { saveImages: async () => [{ id: 'a' }] }
    return undefined
  },
}
const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: 'helper/bin/dsc.exe',
  escPath: 'helper/bin/dsc-esc.exe',
  gatePath: join(tmp, 'halt'),
  shotDir: join(tmp, 'shots'),
})

const byName = new Map(reg.map((t) => [t.name, t]))
const shotTool = byName.get('desktop_screenshot')
const clickTool = byName.get('desktop_click')
const readTool = byName.get('desktop_read_window')

/* ── 选靶子：优先 Tauri（不是 DSH 自己），其次任何够大的非自家窗口 ── */

const listRaw = JSON.parse(
  spawnSync('helper/bin/dsc.exe', ['list_windows'], { encoding: 'utf8', windowsHide: true }).stdout,
)
const listWindows = () => {
  try {
    return JSON.parse(spawnSync('helper/bin/dsc.exe', ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
  } catch {
    return []
  }
}
const exeOf = (w) => String(w.exe ?? '').split(/[\\/]/).pop().toLowerCase()
const ours = (w) => /^(dsc|dsc-esc|textinputhost)\.exe$/.test(exeOf(w))
const usable = (w) => w.width > 400 && w.height > 400 && w.exe && !ours(w)

const win =
  listRaw.windows.find((w) => String(w.class ?? '').includes('Tauri') && usable(w)) ??
  listRaw.windows.find((w) => usable(w) && !/deepseek harness/i.test(exeOf(w)))

if (!win) {
  console.log('  ✗ 找不到可用窗口（需要一个 >400x400 的真实应用窗口）')
  process.exit(1)
}

const ORIG = { x: win.x, y: win.y, w: win.width, h: win.height }
const isTauri = String(win.class ?? '').includes('Tauri')

console.log()
console.log(`  靶子 hwnd=${win.hwnd} class=${win.class} ${ORIG.w}x${ORIG.h} @(${ORIG.x},${ORIG.y})`)
if (!win32Ready()) {
  console.log('  ✗ 无法加载 user32（SetWindowPos 不可用），这个测试没法做')
  process.exit(1)
}

/* 靶子上的一个**无害点**：SuYou 的「剩余流量」标签。
 * 只在移动窗口那个"放行"格子里真的会点一下，所以要挑点了没副作用的元素。
 *
 * 从**桥**取，不从 dsc.exe 取：这个测试跑在 agent shell 里，而 dsc.exe 在这种
 * 启动链下读 Chromium 只有 7 个节点（见第十轮文档），树里根本没有这个标签。
 * 桥不依赖那条链。 */
function findPython() {
  const cands = [
    process.env.DSC_PYTHON,
    join(process.env.USERPROFILE || process.env.HOME || '', '.dsh', 'dsh-runtimes',
      'dsh-primary-runtime', 'dependencies', 'python', 'python.exe'),
  ]
  for (const c of cands) if (c && existsSync(c)) return c
  return 'python'
}

let inertPoint = null
{
  const r = spawnSync(findPython(), ['server/uia-bridge.py', String(win.hwnd), '200'],
    { encoding: 'utf8', windowsHide: true, timeout: 60000 })
  let items = []
  try { items = JSON.parse(String(r.stdout ?? '').trim().split('\n').pop()).items ?? [] } catch { /* 读不到就跳过 */ }
  const el = items.find((it) => it.name === '剩余流量')
  if (el) {
    inertPoint = {
      ix: el.l + Math.floor(el.w / 2) - ORIG.x,
      iy: el.t + Math.floor(el.h / 2) - ORIG.y,
    }
  }
}

const shotNow = async () => {
  const r = await shotTool.execute({ hwnd: win.hwnd }).catch((e) => ({ err: e.message }))
  return r.shotId ?? ''
}
const msgOf = (r) => String(r?.err ?? r?.result ?? r?.text ?? '')

try {
  /* ══ 0. 基线：能拿到 shotId ══ */
  console.log()
  console.log('── 0. 基线 ──')
  const baseShot = await shotNow()
  if (baseShot) ok(`拿到 shotId：${baseShot}`)
  else { bad('拿不到 shotId，后面的格子都没法验'); throw new Error('no shotId') }

  /* ══ 1. 窗口移动 + space=desktop → 必须拒绝，且不能真的点 ══ */
  console.log()
  console.log('── 1. 窗口移动 + space=desktop（必须拒绝） ──')
  const moved = { x: ORIG.x + 60, y: ORIG.y + 40 }
  if (!setWindowPos(win.hwnd, moved.x, moved.y, ORIG.w, ORIG.h)) {
    bad('SetWindowPos 失败，无法移动窗口')
  } else {
    await new Promise((r) => setTimeout(r, 400))
    // 取一个在**新旧矩形里都算在内**的点，否则会被"坐标不在窗口范围内"提前挡掉，
    // 那样即使通过也证明不了几何校验生效。
    const px = moved.x + Math.floor(ORIG.w / 2)
    const py = moved.y + Math.floor(ORIG.h / 2)
    const r = await clickTool.execute({
      hwnd: win.hwnd, x: px, y: py, space: 'desktop', shotId: baseShot, observe: false,
    }).catch((e) => ({ err: e.message }))
    const msg = msgOf(r)
    if (/(移到了|窗口一移)/.test(msg) && /不点/.test(msg)) {
      ok('被拒绝，并说明"窗口移了 → desktop 坐标已失效，所以不点"')
    } else if (/超出窗口|不在窗口/.test(msg)) {
      bad(`被"坐标不在窗口内"挡掉了，没走到几何校验：${msg.slice(0, 80)}`)
    } else {
      bad(`窗口移动后仍然放行了：${msg.slice(0, 100)}`)
    }

    /* ══ 1b. 被拒绝的点击不能把 shotId 烧掉 ══
     *
     * 这一格是 2.18.31 补的：原来只要调用了变更性工具，收尾时一律作废旧截图，
     * 连"被拒绝、什么都没发生"也作废。于是出现自相矛盾的一段：
     *   插件："窗口移了……你可以改用 space=\"window\" 重试"
     *   调用方改用 space="window" → "找不到截图记录"
     * 插件给的建议自己走不通。
     */
    console.log()
    console.log('── 1b. 被拒绝的那次不该作废这张截图 ──')
    {
      const again = await clickTool.execute({
        hwnd: win.hwnd, x: px, y: py, space: 'desktop', shotId: baseShot, observe: false,
      }).catch((e) => ({ err: e.message }))
      const m = msgOf(again)
      if (/找不到截图/.test(m)) {
        bad('被拒绝之后 shotId 就失效了 —— 插件建议的"改用 space=window 重试"会因此走不通')
      } else if (/(移到了|窗口一移)/.test(m)) {
        ok('shotId 仍然有效，同一个拒绝原因又出现了一次（这是预期）')
      } else {
        bad(`预期仍然是被拒绝，实际：${m.slice(0, 100)}`)
      }
    }

    /* ══ 2. 仍然是移动状态，但 space=window → 放行 + 警告 ══ */
    console.log()
    console.log('── 2. 窗口移动 + space=window（应放行并警告） ──')
    if (!inertPoint) {
      skip('树里找不到无害点（剩余流量），这一格没法安全验证')
    } else {
      const r2 = await clickTool.execute({
        hwnd: win.hwnd, x: inertPoint.ix, y: inertPoint.iy, space: 'window',
        shotId: baseShot, observe: false,
      }).catch((e) => ({ err: e.message }))
      const m2 = msgOf(r2)
      if (/移到了/.test(m2) && /仍然有效/.test(m2)) {
        ok('放行了，并且明确说明"原点按当前矩形算，坐标仍然有效"')
      } else if (/SetCursorPos|拒绝访问/.test(m2)) {
        skip(`几何校验**已经放行**（走到了注入这一步），但这台机器的沙箱拦下 SetCursorPos，`
          + '看不到"放行+警告"那段文字')
      } else {
        bad(`期望"放行+警告"，实际：${m2.slice(0, 100)}`)
      }
    }
    if (!setWindowPos(win.hwnd, ORIG.x, ORIG.y, ORIG.w, ORIG.h)) {
      bad('窗口位置复原失败 —— 请手动把窗口挪回原位')
    }
    await new Promise((r) => setTimeout(r, 400))
  }

  /* ══ 3. 窗口改大小 → 必须拒绝 ══ */
  console.log()
  console.log('── 3. 窗口改大小（必须拒绝） ──')
  const shotBeforeResize = await shotNow()
  if (!shotBeforeResize) {
    skip('拿不到新 shotId')
  } else if (!setWindowPos(win.hwnd, ORIG.x, ORIG.y, ORIG.w - 120, ORIG.h)) {
    bad('SetWindowPos 缩放失败')
  } else {
    await new Promise((r) => setTimeout(r, 400))
    const r = await clickTool.execute({
      hwnd: win.hwnd, x: 5, y: 5, space: 'window', shotId: shotBeforeResize, observe: false,
    }).catch((e) => ({ err: e.message }))
    const msg = msgOf(r)
    if (/尺寸变了|尺寸从/.test(msg)) ok('被拒绝，并说明"尺寸变了，旧坐标不可靠"')
    else bad(`窗口改大小后仍然放行了：${msg.slice(0, 100)}`)
    if (!setWindowPos(win.hwnd, ORIG.x, ORIG.y, ORIG.w, ORIG.h)) {
      bad('窗口尺寸复原失败 —— 请手动调回')
    }
    await new Promise((r) => setTimeout(r, 400))
  }

  /* ══ 4. shotId 属于别的窗口 → 必须拒绝 ══ */
  console.log()
  console.log('── 4. shotId 与目标窗口对不上（必须拒绝） ──')
  {
    const other = listRaw.windows.find((w) => usable(w) && String(w.hwnd) !== String(win.hwnd))
    if (!other) {
      skip('没有第二个可用窗口')
    } else {
      const s = await shotNow()
      const r = await clickTool.execute({
        hwnd: other.hwnd, x: 5, y: 5, space: 'window', shotId: s, observe: false,
      }).catch((e) => ({ err: e.message }))
      const msg = msgOf(r)
      if (/是窗口/.test(msg) && /不能混用/.test(msg)) ok('被拒绝，说明两个窗口的坐标不能混用')
      else bad(`不同窗口的 shotId 被接受了：${msg.slice(0, 100)}`)
    }
  }

  /* ══ 5. 落点被别的窗口盖住 → 必须拒绝，且不能去点遮挡者 ══ */
  console.log()
  console.log('── 5. 落点被置顶窗口盖住（必须拒绝，不许点遮挡者） ──')
  {
    /* 遮挡者必须**自己造**，原因有两个：
     *   · 必须是 topmost —— 否则 helper 激活目标窗口之后落点又变回目标，
     *     那一格就成了"照常点成功"，什么都没验到；
     *   · 不能用用户的窗口 —— 置顶 + 关掉别人的窗口是不可接受的副作用。
     * 所以起一个临时的无边框 WinForms 窗体，盖住落点，9 秒后自己关。
     */
    const px = ORIG.x + Math.floor(ORIG.w / 2)
    const py = ORIG.y + Math.floor(ORIG.h / 2)
    const coverScript =
      PS_PRELUDE +
      `Add-Type -Namespace W -Name S -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'; ` +
      `Add-Type -AssemblyName System.Windows.Forms; ` +
      `Add-Type -AssemblyName System.Drawing; ` +
      `$f = New-Object System.Windows.Forms.Form; ` +
      `$f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.TopMost = $true; ` +
      `$f.ShowInTaskbar = $false; $f.BackColor = 'Black'; ` +
      `$f.Location = New-Object System.Drawing.Point(${px - 160}, ${py - 110}); ` +
      `$f.Size = New-Object System.Drawing.Size(320, 220); $f.Show(); ` +
      // 显式 SW_SHOWNORMAL：不依赖启动信息里的显示状态。
      `[void][W.S]::ShowWindow($f.Handle, 5); ` +
      `$end = (Get-Date).AddSeconds(9); ` +
      `while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 80 }; ` +
      `$f.Close()`
    const bg = spawnDetached(coverScript)
    // 等它真的盖住那个点：判据用 WindowFromPoint，不用窗口列表
    // （ShowInTaskbar=$false 的窗体不会出现在列表里）。
    let covered = false
    for (let i = 0; i < 16 && !covered; i++) {
      await new Promise((r) => setTimeout(r, 250))
      const root = rootAt(px, py)
      covered = root !== 0 && String(root) !== String(win.hwnd)
    }
    if (!covered) {
      skip('临时遮挡窗口没能盖住落点，这一格没验')
      try { bg?.kill() } catch { /* ignore */ }
    } else {
      await new Promise((r) => setTimeout(r, 300))
      const r = await clickTool.execute({
        hwnd: win.hwnd, x: px, y: py, space: 'desktop', observe: false,
      }).catch((e) => ({ err: e.message }))
      const msg = msgOf(r)
      if (/不是指定的目标窗口/.test(msg) && /不会替你去点/.test(msg)) {
        ok('被拒绝，并且明确写了"不会替你去点那个盖住它的窗口"')
      } else if (/Refused/.test(msg)) {
        skip(`拒绝了，但没走到"落点属于谁"那条判断：${msg.slice(0, 70)}`)
      } else if (/cursorLanded|已发出/.test(msg)) {
        bad(`落点被盖住却点下去了：${msg.slice(0, 120)}`)
      } else {
        bad(`预期之外的返回：${msg.slice(0, 120)}`)
      }
      try { bg?.kill() } catch { /* 它也会自己关 */ }
      await new Promise((r) => setTimeout(r, 400))
    }
  }

  /* ══ 6. 剩下两条没验的 ══ */
  console.log()
  console.log('── 6. 本文件没覆盖的两条 ──')
  console.log('    · 过期（5 分钟）另有一个专门的测试：tools/test-shot-ttl.mjs'
    + '（用 DSC_SHOT_TTL_MS 把保鲜期调成 1ms 就能立刻验）')
  skip('窗口被关掉 → StaleTarget 这一格没验（要关掉一个应用，代价大于收益）')
} finally {
  // 无论哪一格炸了，都必须把窗口放回去。
  setWindowPos(win.hwnd, ORIG.x, ORIG.y, ORIG.w, ORIG.h)
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
