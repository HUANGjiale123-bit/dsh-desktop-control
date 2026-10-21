#!/usr/bin/env node
/* 回归：guard.ready 的判据必须是"本轮 + 真看门狗"，不能接受旧格式或无关进程。
 *
 * 背景（2.18.73 复核 P1）：旧判据放行了纯 PID 文件、缺 runId、空 runId，
 * 而且只验证 PID 存活 —— 任何活着的进程都能充当"恢复保障"。
 * 现在要求 runId 非空且精确匹配、PID 严格正整数、进程启动时间一致。
 *
 * 这里直接调用**打包的 dsc-fx.exe --ready-check**（只读，不动光标），
 * 用真实进程做正反例。
 *
 * 用法：node tools/test-ready-check.mjs
 */

import { existsSync, mkdirSync, rmSync, writeFileSync, readFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { tempHelper, assertNotLow } from './fixture.mjs'

const HELPER = tempHelper()
assertNotLow(HELPER)

const FX = join(process.cwd(), 'helper', 'bin', 'dsc-fx.exe')
const GUARD = join(process.cwd(), 'helper', 'bin', 'dsc-guard.exe')
const DIR = join(process.cwd(), '.cache', 'ready-check')
const RUN = 'run-' + Date.now()

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/** 调 fx 的只读自检；返回 true/false。 */
const accepts = (run) => {
  const r = spawnSync(FX, ['--ready-check', DIR, run], { encoding: 'utf8', windowsHide: true, timeout: 10000 })
  try { return JSON.parse(r.stdout).ok === true } catch { return false }
}
const writeReady = (obj) => writeFileSync(join(DIR, 'guard.ready'), typeof obj === 'string' ? obj : JSON.stringify(obj))

try {
  rmSync(DIR, { recursive: true, force: true })
  mkdirSync(DIR, { recursive: true })
  if (!existsSync(FX)) { console.log('  · 找不到 dsc-fx.exe'); process.exit(0) }

  console.log()
  console.log('── 1. 各种"不算数"的 ready 都必须拒绝 ──')
  if (!accepts(RUN)) ok('没有 ready 文件 → 拒绝')

  writeReady(String(process.pid))
  if (!accepts(RUN)) ok('旧格式（纯 PID）→ 拒绝')
  else bad('旧格式被接受')

  writeReady({ pid: process.pid })
  if (!accepts(RUN)) ok('JSON 只有 pid、没有 runId → 拒绝')
  else bad('缺 runId 被接受')

  writeReady({ runId: '', pid: process.pid })
  if (!accepts(RUN)) ok('runId 为空 → 拒绝')
  else bad('空 runId 被接受')

  writeReady({ runId: 'another-run', pid: process.pid })
  if (!accepts(RUN)) ok('runId 不匹配 → 拒绝')
  else bad('别的 runId 被接受')

  writeReady({ runId: RUN, pid: process.pid, start: 1 })
  if (!accepts(RUN)) ok('runId 匹配但进程启动时间不对（无关进程）→ 拒绝')
  else bad('无关进程被当成看门狗')

  writeReady({ runId: RUN, pid: -process.pid, start: 1 })
  if (!accepts(RUN)) ok('负 PID → 拒绝')
  else bad('负 PID 被接受')

  writeReady('{"runId":"' + RUN + '","pid":abc,"start":1}')
  if (!accepts(RUN)) ok('非法 PID → 拒绝')
  else bad('非法 PID 被接受')

  console.log()
  console.log('── 2. 真正的本轮 guard：接受 ──')
  // 起一个真 guard，用同一个 runId；它写出的 ready 必须被接受
  const { spawn } = await import('node:child_process')
  const guard = spawn(GUARD, ['--watch', DIR, '--run', RUN], { stdio: 'ignore', windowsHide: true })
  let readyOk = false
  for (let i = 0; i < 30 && !readyOk; i++) { await sleep(100); readyOk = existsSync(join(DIR, 'guard.ready')) }
  if (readyOk) ok('guard 写了 ready')
  else bad('guard 没写 ready')

  const text = existsSync(join(DIR, 'guard.ready')) ? readFileSync(join(DIR, 'guard.ready'), 'utf8') : ''
  if (/runId/.test(text) && /start/.test(text)) ok('ready 含 runId 与启动时间')
  else bad(`ready 内容不完整：${text.slice(0, 80)}`)

  if (accepts(RUN)) ok('本轮真 guard → 接受')
  else bad('真 guard 反而被拒绝')

  if (!accepts('other-run')) ok('换一个 runId → 拒绝（同一份 ready）')
  else bad('同一份 ready 被别的 run 接受')

  try { guard.kill() } catch { }
} finally {
  try { spawnSync('taskkill', ['/IM', 'dsc-guard.exe', '/F'], { windowsHide: true }) } catch { }
  try { rmSync(DIR, { recursive: true, force: true }) } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
