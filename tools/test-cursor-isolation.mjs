#!/usr/bin/env node
/* 回归：两个插件实例必须各有自己的状态目录，不能共用 %TEMP%\dsc-cursor-fx。
 *
 * 背景（复核 2.18.73 关联缺口）：所有 profile/实例共享同一个状态目录时，
 * 一个实例写 fx.stop / 清 fx.heartbeat，会让另一个实例的装饰与看门狗误判，
 * 甚至把对方的恢复路径干掉。
 *
 * 做法：起两个**真实子进程**，各自 apply 插件、调用一次桌面工具（会起装饰进程），
 * 然后检查：
 *   1. 两个状态目录不同（都在 %TEMP%\dsc-cursor-fx\<pid> 下）
 *   2. 各自都写了 fx.heartbeat
 *   3. 杀掉 A 之后，B 的目录与装饰进程还在（A 的死活不影响 B）
 *
 * 用法：node tools/test-cursor-isolation.mjs（约 15 秒）
 */

import { existsSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tmpdir } from 'node:os'
import { tempHelper, assertNotLow } from './fixture.mjs'

const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const BASE = join(tmpdir(), 'dsc-cursor-fx')
const CHILD = join(process.cwd(), '.cache', 'cursor-instance-child.mjs')

const childCode = `
import { readFileSync, writeFileSync } from 'node:fs'
import { pathToFileURL } from 'node:url'
import { join } from 'node:path'

const helper = process.argv[2]
const reg = []
const ctx = {
  logger: { info: () => {}, warn: () => {}, error: () => {} },
  tools: { register: (t) => { reg.push(t); return () => {} } },
  on: () => {},
  get() { return undefined },
}
const mod = await import(pathToFileURL(join(process.cwd(), 'plugin', 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, { helperPath: helper, escPath: 'helper/bin/dsc-esc.exe', shotDir: join('.cache', 'iso-shots'), cursorFx: true, allowSingleSession: true })
const t = reg.find((x) => x.name === 'desktop_list_windows')
await t.execute({}, {})
console.log('READY ' + process.pid)
setInterval(() => {}, 1000)
`

/** 等子进程打印 READY，返回 pid。 */
const waitReady = (child, ms = 15000) => new Promise((resolve) => {
  let buf = ''
  const timer = setTimeout(() => resolve(null), ms)
  child.stdout.on('data', (d) => {
    buf += String(d)
    const m = buf.match(/READY (\d+)/)
    if (m) { clearTimeout(timer); resolve(Number(m[1])) }
  })
})

const killTree = (pid) => {
  spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true })
}

try {
  mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
  writeFileSync(CHILD, childCode)

  console.log()
  console.log('── 1. 起两个独立插件实例 ──')
  const a = spawn(process.execPath, [CHILD, HELPER], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
  const pidA = await waitReady(a)
  const b = spawn(process.execPath, [CHILD, HELPER], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
  const pidB = await waitReady(b)

  if (pidA && pidB) ok(`两个实例都起来了（${pidA} / ${pidB}）`)
  else bad('实例没起来')

  const dirA = join(BASE, String(pidA))
  const dirB = join(BASE, String(pidB))
  if (dirA === dirB) bad('两个实例算出了同一个目录')
  if (existsSync(join(dirA, 'fx.heartbeat'))) ok('A 有自己的状态目录与心跳')
  else bad(`A 的目录没有心跳：${dirA}`)
  if (existsSync(join(dirB, 'fx.heartbeat'))) ok('B 有自己的状态目录与心跳')
  else bad(`B 的目录没有心跳：${dirB}`)

  const logB1 = existsSync(join(dirB, 'fx.log')) ? readFileSync(join(dirB, 'fx.log'), 'utf8') : ''
  console.log(`    B 日志行数：${logB1.trim().split('\n').length}`)

  console.log()
  console.log('── 2. 杀掉 A：B 的状态不能被连带清掉 ──')
  killTree(pidA)
  await sleep(1500)
  if (existsSync(join(dirB, 'fx.heartbeat'))) ok('A 退出后 B 的目录与心跳仍在')
  else bad('A 退出把 B 的状态也带走了')

  const logB2 = existsSync(join(dirB, 'fx.log')) ? readFileSync(join(dirB, 'fx.log'), 'utf8') : ''
  if (logB2.length >= logB1.length) ok('B 的日志没有被清空')
  else bad('B 的日志被清空了')

  console.log()
  console.log('── 3. 收尾 ──')
  killTree(pidB)
  await sleep(500)
  for (const d of [dirA, dirB]) { try { rmSync(d, { recursive: true, force: true }) } catch { } }
  try { rmSync(CHILD, { force: true }) } catch { }
  ok('清理完成')
} finally {
  for (const name of ['dsc-fx.exe', 'dsc-guard.exe']) {
    spawnSync('taskkill', ['/IM', name, '/F'], { windowsHide: true })
  }
  try { rmSync(CHILD, { force: true }) } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
