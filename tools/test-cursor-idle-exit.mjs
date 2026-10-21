#!/usr/bin/env node
/* 回归：插件**还在**但一段时间没有桌面操作时，dsc-fx / dsc-guard 必须自己退出。
 *
 * 为什么单独有这条：
 *
 *   上一版用 setInterval 每 5 秒续心跳，验证时只测了"心跳断了会退"，
 *   却没测"心跳会不会断"。第三方验收实测：HMR 卸载插件不走 dispose，
 *   那个定时器留在宿主进程里继续写心跳 —— 于是自退永远不触发，
 *   卸载后留下两个孤儿，还多了一个每 5 秒写盘的泄漏。
 *
 *   现在心跳只在真实工具调用时写，**没有任何定时器**。
 *   这个测试就是那个缺失的判据：插件照常加载、但不再被使用 → 两个孩子必须退出。
 *
 * 用法：node tools/test-cursor-idle-exit.mjs（约 45 秒）
 */

import { existsSync, readFileSync, statSync, mkdtempSync, mkdirSync, writeFileSync, rmSync, copyFileSync } from 'node:fs'
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


const PS_ABS = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const STATE = join(process.cwd(), '.cache', 'idle-exit-state')
// 命令行里用作识别特征的就是这个 watch 目录
const NEEDLE = STATE
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'idle-exit-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}
mkdirSync(join(tmp, 'bin'), { recursive: true })
mkdirSync(join(tmp, 'assets', 'cursor'), { recursive: true })
for (const exe of ['dsc-fx.exe', 'dsc-guard.exe']) {
  copyFileSync(join('helper', 'bin', exe), join(tmp, 'bin', exe))
}
copyFileSync(join('plugin', 'assets', 'cursor', 'fish-arrow.png'), join(tmp, 'assets', 'cursor', 'fish-arrow.png'))
const BEAT = join(STATE, 'fx.heartbeat')

// 只认"自己起的"进程：按命令行里的 watch 目录过滤。
// 按进程名判断会误伤机器上真正安装的插件（实测：残留进程把我们自己的判断带偏）。
const oursAlive = (name) => {
  const r = spawnSync(PS_ABS, ['-NoProfile', '-Command',
    `(Get-CimInstance Win32_Process -Filter "Name='${name}'" | Where-Object { $_.CommandLine -like '*${NEEDLE}*' } | Measure-Object).Count`],
  { encoding: 'utf8', windowsHide: true, timeout: 15000 })
  return Number(String(r.stdout).trim()) > 0
}
const killOurs = () => {
  spawnSync(PS_ABS, ['-NoProfile', '-Command',
    `Get-CimInstance Win32_Process -Filter "Name='dsc-fx.exe'" | Where-Object { $_.CommandLine -like '*${NEEDLE}*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
     Get-CimInstance Win32_Process -Filter "Name='dsc-guard.exe'" | Where-Object { $_.CommandLine -like '*${NEEDLE}*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }`],
  { windowsHide: true, timeout: 15000 })
}

try {
  killOurs()
  mkdirSync(STATE, { recursive: true })
  try { rmSync(BEAT, { force: true }) } catch { }

  const reg = []
  const ctx = {
    logger: { info: () => {}, warn: () => {}, error: () => {} },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    on: () => {},
    get(n) {
      if (n === 'approval') return { request: async () => 'allowed-once' }
      if (n === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
      if (n === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['s1'] }] }
      return undefined
    },
  }
  const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots'),
    cursorFx: true, cursorFxDir: STATE,
  })
  const by = new Map(reg.map((t) => [t.name, t]))

  console.log()
  console.log('── 1. 一次桌面操作：两个进程起来、心跳写入 ──')
  await by.get('desktop_list_windows').execute({}).catch(() => {})
  await sleep(1500)
  if (oursAlive('dsc-fx.exe')) ok('dsc-fx 已启动')
  else bad('dsc-fx 没启动')
  if (!existsSync(BEAT)) { bad('心跳文件没写'); }
  else ok(`心跳已写入（${readFileSync(BEAT, 'utf8').trim().slice(0, 13)}…）`)
  const beatAt = statSync(BEAT).mtimeMs

  console.log()
  console.log('── 2. 插件继续活着，但不再有桌面操作 ──')
  console.log('    关键断言：心跳不能被刷新（这是上一版漏掉的判据）')
  let fxGone = -1
  const t0 = Date.now()
  while (Date.now() - t0 < 45000) {
    await sleep(1500)
    if (fxGone < 0 && !oursAlive('dsc-fx.exe')) fxGone = Date.now() - t0
    if (fxGone > 0) break
  }

  const beatAfter = existsSync(BEAT) ? statSync(BEAT).mtimeMs : -1
  if (beatAfter === beatAt) ok('心跳没有被后台定时器继续刷新')
  else bad(`心跳被刷新了（${Math.round((beatAfter - beatAt) / 1000)} 秒后）—— 说明还有定时器在写`)

  // 新语义（复核 2.18.72）：恢复成功才退出；恢复失败时保留进程继续重试是**正确**的。
  // 所以判据是：要么已退出，要么仍在、但标记还在（说明它在负责恢复）。
  const markerLeft = existsSync(join(STATE, 'fx.blanked'))
  if (fxGone >= 0) ok(`dsc-fx 在无操作后自行退出（${Math.round(fxGone / 1000)} 秒）`)
  else if (oursAlive('dsc-fx.exe') && markerLeft) ok('dsc-fx 仍在，但保留待恢复标记（继续持有恢复责任）')
  else bad('45 秒后既没退出、也没有待恢复标记 —— 状态不明确')

  if (!oursAlive('dsc-guard.exe')) ok('dsc-guard 已退出')
  else if (markerLeft) ok('dsc-guard 仍在（有待恢复标记，继续负责恢复）')
  else bad('dsc-guard 仍在运行且没有待恢复标记')

  console.log()
  console.log('── 3. 再次调用时能重新拉起（可用性没被破坏）──')
  await by.get('desktop_list_windows').execute({}).catch(() => {})
  await sleep(1800)
  if (oursAlive('dsc-fx.exe')) ok('再次调用后 dsc-fx 重新启动')
  else bad('再次调用后没有重新启动 —— 功能被"自退"破坏了')
} finally {
  killOurs()
  // 这个测试真的会让插件隐藏系统光标：收尾必须恢复（实事故：曾把用户光标弄消失）
  try { spawnSync(HELPER, ['cursor_restore'], { windowsHide: true, timeout: 15000 }) } catch { }
  try { rmSync(tmp, { recursive: true, force: true }) } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
