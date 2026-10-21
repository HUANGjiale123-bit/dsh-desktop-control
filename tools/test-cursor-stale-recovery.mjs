#!/usr/bin/env node
/* 回归：插件启动时，必须恢复"上一实例被同时强杀"留下的待恢复状态。
 *
 * 背景（发布前复核）：看门狗能救"fx 单独死"，但 fx 与 guard **一起**被强杀时，
 * 系统光标可能一直透明，而且没有任何进程会去恢复。
 * 现在插件加载时会扫 %TEMP%\dsc-cursor-fx\*\fx.blanked：
 * 记录里的 pid 已不存在 → 调一次 cursor_restore 并清掉该死实例的目录；
 * 活着的实例一律不动。
 *
 * 用法：node tools/test-cursor-stale-recovery.mjs
 */

import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
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
const deadDir = join(BASE, 'stale-test-dead-' + process.pid)
const liveDir = join(BASE, 'stale-test-live-' + process.pid)
const DEAD_PID = 999999 // 几乎不可能存在的 pid

try {
  mkdirSync(deadDir, { recursive: true })
  mkdirSync(liveDir, { recursive: true })
  writeFileSync(join(deadDir, 'fx.blanked'), String(DEAD_PID))
  writeFileSync(join(liveDir, 'fx.blanked'), String(process.pid))

  console.log()
  console.log('── 1. 加载插件（会触发启动恢复）──')
  const reg = []
  const warns = []
  const ctx = {
    logger: { info: () => {}, warn: (m) => warns.push(String(m)), error: () => {} },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    on: () => {},
    get() { return undefined },
  }
  const mod = await import(pathToFileURL(join(process.cwd(), 'plugin', 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join('.cache', 'stale-shots'),
    allowSingleSession: true,
  })
  await sleep(2000)

  if (warns.some((w) => /待恢复标记|恢复系统光标/.test(w))) ok('日志里明确报告了发现残留并恢复')
  else bad(`没有看到恢复日志：${warns.join(' | ').slice(0, 120)}`)

  if (!existsSync(deadDir)) ok('已死实例的残留目录被清理')
  else bad('已死实例的目录还在')

  if (existsSync(liveDir)) ok('活实例（pid 存活）的目录没有被误删')
  else bad('活实例的目录被误删了')

  console.log()
  console.log('── 2. 没有残留时不该打扰任何人 ──')
  rmSync(liveDir, { recursive: true, force: true })
  const warnsBefore = warns.length
  const mod2 = await import(pathToFileURL(join(process.cwd(), 'plugin', 'index.mjs')).href + '?' + Math.random())
  await mod2.apply(ctx, { helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join('.cache', 'stale-shots'), allowSingleSession: true })
  await sleep(500)
  const newWarns = warns.slice(warnsBefore).filter((w) => /待恢复标记/.test(w))
  if (newWarns.length === 0) ok('没有残留时不产生恢复告警')
  else bad('没有残留却报了恢复')
} finally {
  for (const d of [deadDir, liveDir]) { try { rmSync(d, { recursive: true, force: true }) } catch { } }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
