#!/usr/bin/env node
/* 回归：正常 dispose 时，光标必须先**确认恢复**，进程才退出；且不留停止标记。
 *
 * 背景（复核 R5）：旧 stop() 是"写 stop → 立刻 kill fx → 立刻 kill guard → taskkill /IM"。
 * Windows 上 child.kill() 是强制结束，fx 的 finally 可能没跑，恢复就没发生；
 * taskkill /IM 还会杀掉机器上所有实例（包括另一个 profile 的）。
 *
 * 这个测试测的是**正常卸载路径**，不是空闲自退（那条有独立的 test-cursor-idle-exit）。
 *
 * 用法：node tools/test-cursor-dispose.mjs（约 15 秒）
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, copyFileSync, rmSync, existsSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

// ── 安全闸门：这些测试会真的隐藏系统光标 ──
//
// 实事故：测试在用户机器上隐藏了光标，清理时直接杀进程，恢复没跑完。
// 现在默认**不运行**；只有显式设 DSC_REAL_CURSOR_TESTS=1 才执行。
if (process.env.DSC_REAL_CURSOR_TESTS !== '1') {
  console.log('  · 会真改系统光标：设 DSC_REAL_CURSOR_TESTS=1 才运行（默认跳过）')
  console.log('  0 项通过，0 项失败，1 项跳过')
  process.exit(0)
}


const PS = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const STATE = join(process.cwd(), '.cache', 'dispose-state')
const NEEDLE = STATE

const oursAlive = (name) => {
  const r = spawnSync(PS, ['-NoProfile', '-Command',
    `(Get-CimInstance Win32_Process -Filter "Name='${name}'" | Where-Object { $_.CommandLine -like '*${NEEDLE}*' } | Measure-Object).Count`],
  { encoding: 'utf8', windowsHide: true, timeout: 15000 })
  return Number(String(r.stdout).trim()) > 0
}
const killOurs = () => {
  spawnSync(PS, ['-NoProfile', '-Command',
    `foreach ($n in @('dsc-fx.exe','dsc-guard.exe')) { Get-CimInstance Win32_Process -Filter "Name='$n'" | Where-Object { $_.CommandLine -like '*${NEEDLE}*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }`],
  { windowsHide: true, timeout: 15000 })
}

const boot = async () => {
  const tmp = mkdtempSync(join(process.cwd(), '.cache', 'dispose-'))
  for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
    writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
  }
  mkdirSync(join(tmp, 'bin'), { recursive: true })
  mkdirSync(join(tmp, 'assets', 'cursor'), { recursive: true })
  for (const exe of ['dsc-fx.exe', 'dsc-guard.exe']) {
    copyFileSync(join('helper', 'bin', exe), join(tmp, 'bin', exe))
  }
  copyFileSync(join('plugin', 'assets', 'cursor', 'fish-arrow.png'), join(tmp, 'assets', 'cursor', 'fish-arrow.png'))

  const reg = []
  const disposers = []
  const ctx = {
    logger: { info: () => {}, warn: () => {}, error: () => {} },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    on: (ev, fn) => { if (ev === 'dispose') disposers.push(fn) },
    get() { return undefined },
  }
  const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots'),
    cursorFx: true, cursorFxDir: STATE, allowSingleSession: true,
  })
  const tool = reg.find((t) => t.name === 'desktop_list_windows')
  return { tmp, tool, disposers }
}

try {
  killOurs()
  mkdirSync(STATE, { recursive: true })
  for (const f of ['fx.blanked', 'fx.stop', 'fx.heartbeat', 'fx.active']) {
    try { rmSync(join(STATE, f), { force: true }) } catch { }
  }

  console.log()
  console.log('── 1. 起一次操作，让装饰接管光标 ──')
  const first = await boot()
  await first.tool.execute({}, {})
  await sleep(2000)
  const blanked = join(STATE, 'fx.blanked')
  if (existsSync(blanked)) ok('装饰已接管（fx.blanked 存在）')
  else bad('装饰没有接管，后续断言没有意义')

  console.log()
  console.log('── 2. 触发真实 dispose ──')
  const t0 = Date.now()
  for (const fn of first.disposers) { try { await fn() } catch (e) { console.log(`    dispose 抛错：${e?.message ?? e}`) } }
  const took = Date.now() - t0

  // 关键断言：dispose 返回时，光标必须已经恢复（blanked 已清）
  if (existsSync(blanked)) {
    try {
      const lines = readFileSync(join(STATE, 'fx.log'), 'utf8').trim().split('\n')
      console.log('    fx.log:', lines.slice(-3).join(' | '))
    } catch { }
  }
  if (!existsSync(blanked)) ok(`dispose 返回时光标已恢复（耗时 ${Math.round(took / 1000)} 秒）`)
  else bad('dispose 返回时 fx.blanked 仍在 —— 恢复没有得到确认')

  await sleep(1000)
  if (!oursAlive('dsc-fx.exe')) ok('自己的装饰进程已退出')
  else bad('装饰进程仍在运行')
  if (!oursAlive('dsc-guard.exe')) ok('自己的看门狗已退出')
  else bad('看门狗仍在运行')

  console.log()
  console.log('── 3. 不能留下停止标记（否则下一次启动会被立刻叫停）──')
  if (!existsSync(join(STATE, 'fx.stop'))) ok('fx.stop 已清理')
  else bad('fx.stop 残留')

  console.log()
  console.log('── 4. 再次加载并操作：装饰必须能重新启动 ──')
  const second = await boot()
  await second.tool.execute({}, {})
  await sleep(2000)
  if (oursAlive('dsc-fx.exe')) ok('重新加载后装饰能启动（没有带着旧标记）')
  else bad('重新加载后装饰起不来 —— 旧停止标记影响了新实例')
  for (const fn of second.disposers) { try { await fn() } catch { } }
  try { rmSync(first.tmp, { recursive: true, force: true }) } catch { }
  try { rmSync(second.tmp, { recursive: true, force: true }) } catch { }
} finally {
  killOurs()
  spawnSync(HELPER, ['cursor_restore'], { windowsHide: true, timeout: 15000 })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
