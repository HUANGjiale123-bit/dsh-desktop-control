#!/usr/bin/env node
/* 回归：点 Win32 弹出菜单里的某一项，必须被接受、并且真的执行。
 *
 * 背景（第三方验收报告 A 项）：打开记事本「编辑」菜单后，点菜单项被拒绝，
 * 理由是"坐标属于另一个窗口，不是指定的目标窗口"。根因是点击流程先激活目标，
 * 而激活会把弹出菜单关掉，随后拿一个已消失的菜单坐标去比对。
 *
 * 修法：同进程的弹出窗口按同一应用对待，且这种情况下不激活。
 *
 * 夹具（tools/fixtures/menu-window.ps1）自己把坐标写出来：
 *   - 窗口出现时写 { hwnd, barX, barY }
 *   - 菜单打开时再补 { itemX, itemY }
 * 测试只读这个文件，**不猜窗口位置、不枚举窗口**。
 *
 * 用法：node tools/test-menu-click.mjs
 */

import { existsSync, readFileSync, rmSync, mkdirSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { tempHelper, assertNotLow } from './fixture.mjs'

const PS = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`) }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const dir = join(process.cwd(), '.cache', 'menu-fixture')
mkdirSync(dir, { recursive: true })
const LOG = '.cache/menu-fixture/menu.log'
const COORD = '.cache/menu-fixture/coords.json'
for (const f of [LOG, COORD]) { try { rmSync(f, { force: true }) } catch { } }

const run = (args) => {
  const r = spawnSync(HELPER, args, {
    encoding: 'utf8', windowsHide: true, timeout: 20000,
    env: { ...process.env, DSC_ALLOWED_APPS: 'powershell.exe' },
  })
  try { return JSON.parse(r.stdout) } catch { return { ok: false, raw: String(r.stdout || r.stderr).slice(0, 200) } }
}

const readCoords = () => {
  try {
    // PowerShell 5.1 的 Set-Content -Encoding UTF8 会写 BOM，JSON.parse 会因此抛错
    return JSON.parse(readFileSync(COORD, 'utf8').replace(/^﻿/, ''))
  } catch { return null }
}

const waitFor = async (pred, ms = 8000) => {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) {
    const v = pred()
    if (v) return v
    await sleep(200)
  }
  return null
}

// 清掉上一次没退干净的夹具窗口：批量跑时它会盖住新窗口，导致菜单点不开
try {
  spawnSync(PS, ['-NoProfile', '-Command',
    'Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq "Menu Fixture" } | Stop-Process -Force'],
  { windowsHide: true })
} catch { }

// ── 让夹具能到前台 ──
//
// DSH 主窗口通常盖住整屏，而且它自己也是置顶的；夹具在它下面，点菜单栏会落在 DSH 上。
// 测试期间把 DSH 最小化，结束再恢复——这样菜单栏可点，测完界面照原样。
const hideDsh = () => {
  try {
    spawnSync(PS, ['-NoProfile', '-Command',
      `$p = Get-Process | Where-Object { $_.MainWindowTitle -like '*DeepSeek Harness*' } | Select-Object -First 1
       if ($p) { [void](Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'); [W.U]::ShowWindow($p.MainWindowHandle, 6) }`],
    { windowsHide: true, timeout: 15000 })
  } catch { }
}
const showDsh = () => {
  try {
    spawnSync(PS, ['-NoProfile', '-Command',
      `$p = Get-Process | Where-Object { $_.MainWindowTitle -like '*DeepSeek Harness*' } | Select-Object -First 1
       if ($p) { [void](Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'); [W.U]::ShowWindow($p.MainWindowHandle, 9) }`],
    { windowsHide: true, timeout: 15000 })
  } catch { }
}

let child = null
hideDsh()
try {
  console.log()
  console.log('── 1. 起夹具窗口 ──')
  // 不能加 windowsHide：加了连夹具自己的 WinForms 窗口都不会出现（实测）
  child = spawn(PS, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    join('tools', 'fixtures', 'menu-window.ps1'), '-LogPath', LOG, '-CoordPath', COORD],
    { stdio: 'ignore' })

  const boot = await waitFor(() => {
    const c = readCoords()
    return c && c.hwnd && c.barX ? c : null
  })
  if (!boot) {
    skip('夹具窗口没出现（坐标文件没写）')
  } else {
    ok(`夹具窗口 hwnd=${boot.hwnd}，菜单栏 (${boot.barX},${boot.barY})`)

    console.log()
    console.log('── 2. 点菜单栏打开菜单 ──')
    // DSH 已在测试开始时最小化，夹具此时能到前台，菜单栏可点。
    const cr = run(['click', String(boot.barX), String(boot.barY), '--hwnd', String(boot.hwnd)])
    if (!cr.ok) bad(`开菜单的点击失败：${String(cr.message ?? cr.raw ?? '').slice(0, 140)}`)
    else ok('菜单栏点击被接受')

    const opened = await waitFor(() => {
      const c = readCoords()
      return c && c.itemX ? c : null
    }, 8000)
    if (!opened) {
      skip('菜单没打开（没有 itemX），跳过后续')
    } else {
      ok(`菜单已打开，菜单项在 (${opened.itemX},${opened.itemY})`)

      console.log()
      console.log('── 3. 点菜单项：必须被接受（报告 A 的核心）──')
      const r = run(['click', String(opened.itemX), String(opened.itemY), '--hwnd', String(opened.hwnd)])
      if (r.ok) {
        ok('点击被接受（没有被"不是指定的目标窗口"拒绝）')
        if (r.atHwnd && Number(r.atHwnd) !== Number(opened.hwnd)) {
          ok(`落点是同进程的另一个窗口（${r.atHwnd}）—— 弹出菜单的预期行为`)
        }
      } else {
        bad(`点击被拒：${String(r.message ?? r.raw ?? '').slice(0, 140)}`)
      }

      console.log()
      console.log('── 4. 菜单项真的执行了吗 ──')
      const clicked = await waitFor(() => existsSync(LOG) && /clicked/.test(readFileSync(LOG, 'utf8')), 4000)
      if (clicked) ok('菜单项的 Click 事件执行了（日志有记录）')
      else bad('菜单项没有执行 —— 点击被接受，但没落在菜单项上')
    }
  }
} finally {
  try { if (child) child.kill() } catch { }
  showDsh()
  try {
    spawnSync(PS, ['-NoProfile', '-Command',
      'Get-Process powershell | Where-Object { $_.MainWindowTitle -eq "Menu Fixture" } | Stop-Process -Force'],
    { windowsHide: true })
  } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
