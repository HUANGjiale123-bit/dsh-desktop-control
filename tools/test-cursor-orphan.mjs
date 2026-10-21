#!/usr/bin/env node
/* 回归：插件消失后，dsc-fx 与 dsc-guard 必须自己退出，不能留孤儿进程。
 *
 * 背景（第三方实测）：卸载插件后留下 dsc-fx 和 dsc-guard 两个孤儿，
 * 得手动 taskkill；而且顺序错了还会互相拉起（插件每次操作都会 start()）。
 *
 * 修法：插件每 5 秒写一次 fx.heartbeat；插件没了心跳就停，
 *   dsc-fx 25 秒内自己退出并恢复光标，
 *   dsc-guard 连续 30 秒看不到 dsc-fx 且没有待恢复的 marker 也退出。
 *
 * 这个测试**不发任何停止信号**，只是不再续心跳——模拟"插件进程消失了"。
 *
 * 用法：node tools/test-cursor-orphan.mjs（约 40 秒）
 */

import { existsSync, mkdirSync, rmSync, writeFileSync, readFileSync, copyFileSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { tempHelper, assertNotLow } from './fixture.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const HELPER = tempHelper()
assertNotLow(HELPER)

const RUN = 'orphan-test-' + Date.now()
const DIR = join(tmpdir(), 'dsc-orphan-test')
const PS_ABS = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const NEEDLE = DIR
const FX_SRC = join(process.cwd(), 'helper', 'bin', 'dsc-fx.exe')
const GUARD_SRC = join(process.cwd(), 'helper', 'bin', 'dsc-guard.exe')
const ASSET = join(process.cwd(), 'plugin', 'assets', 'cursor', 'fish-arrow.png')
const MARK = join(DIR, 'fx.blanked')
const BEAT = join(DIR, 'fx.heartbeat')

const restore = () => { try { spawnSync(HELPER, ['cursor_restore'], { windowsHide: true }) } catch { } }
// 只杀命令行里带本测试目录的进程：机器上真装着的插件也有同名进程，不能误伤。
const killOurs = () => {
  spawnSync(PS_ABS, ['-NoProfile', '-Command',
    `foreach ($n in @('dsc-fx.exe','dsc-guard.exe')) { Get-CimInstance Win32_Process -Filter "Name='$n'" | Where-Object { $_.CommandLine -like '*${NEEDLE}*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }`],
  { windowsHide: true, timeout: 15000 })
}
const oursAlive = (name) => {
  const r = spawnSync(PS_ABS, ['-NoProfile', '-Command',
    `(Get-CimInstance Win32_Process -Filter "Name='${name}'" | Where-Object { $_.CommandLine -like '*${NEEDLE}*' } | Measure-Object).Count`],
  { encoding: 'utf8', windowsHide: true, timeout: 15000 })
  return Number(String(r.stdout).trim()) > 0
}

try {
  mkdirSync(DIR, { recursive: true })
  killOurs()
  restore()
  for (const f of [MARK, BEAT, join(DIR, 'fx.active'), join(DIR, 'fx.log')]) {
    try { rmSync(f, { force: true }) } catch { }
  }
  copyFileSync(FX_SRC, join(DIR, 'dsc-fx.exe'))
  copyFileSync(GUARD_SRC, join(DIR, 'dsc-guard.exe'))

  console.log()
  console.log('── 1. 启动装饰与看门狗（像插件那样）──')
  // 先起看门狗（同一 runId）：它会写带 runId/pid/启动时间的 ready，
  // 装饰进程看到匹配的 ready 才允许隐藏系统光标（复核 2.18.72/73）。
  const guard = spawn(join(DIR, 'dsc-guard.exe'), ['--watch', DIR, '--run', RUN], { stdio: 'ignore', windowsHide: true })
  for (let i = 0; i < 30 && !existsSync(join(DIR, 'guard.ready')); i++) await sleep(100)
  const fx = spawn(join(DIR, 'dsc-fx.exe'), ['--watch', DIR, 'blank', '120', `${ASSET}@45,102`, '--run', RUN],
    { stdio: 'ignore', windowsHide: true })
  await sleep(1200)
  writeFileSync(join(DIR, 'fx.active'), String(Date.now()))
  writeFileSync(BEAT, String(Date.now()))     // 只写一次，之后不再续：模拟插件消失
  await sleep(1500)

  if (existsSync(MARK)) ok('装饰进程已接管光标')
  else bad('装饰进程没接管光标，后面的判断没有意义')

  await sleep(1000)
  if (oursAlive('dsc-fx.exe') && oursAlive('dsc-guard.exe')) ok('两个进程都在跑')
  else bad('进程没起来')

  console.log()
  console.log('── 2. 不再续心跳（插件消失）──')
  console.log('    等它们自己退出（fx 25 秒 / guard 30 秒）…')

  let fxGone = -1
  let guardGone = -1
  const t0 = Date.now()
  while (Date.now() - t0 < 70000) {
    await sleep(1000)
    const since = Date.now() - t0
    if (fxGone < 0 && !oursAlive('dsc-fx.exe')) fxGone = since
    if (guardGone < 0 && !oursAlive('dsc-guard.exe')) guardGone = since
    if (fxGone > 0 && guardGone > 0) break
  }

  if (fxGone >= 0) ok(`dsc-fx 已自行退出（${Math.round(fxGone / 1000)} 秒）`)
  else bad('70 秒内 dsc-fx 没退出 —— 卸载后会留孤儿进程')

  if (guardGone >= 0) ok(`dsc-guard 已自行退出（${Math.round(guardGone / 1000)} 秒）`)
  else bad('70 秒内 dsc-guard 没退出 —— 卸载后会留孤儿进程')

  console.log()
  console.log('── 3. 光标必须已恢复 ──')
  if (!existsSync(MARK)) ok('fx.blanked 已清除（光标已恢复）')
  else bad('marker 还在，光标可能仍是透明的')
} finally {
  killOurs()
  restore()
  rmSync(DIR, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
