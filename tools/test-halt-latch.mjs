#!/usr/bin/env node
/* 回归：急停默认**锁存**，不因时间自动解除。
 *
 * 背景（复核 R3）：JS 与 C# 两端的默认 TTL 都还是 60 秒，而安装说明和工具描述
 * 写的是"没有超时解除这回事"。实测：创建一份 70 秒前的闸门，`dsc.exe stop`
 * 返回 halted:false —— 文件还在，原生端已经当成解除了。
 *
 * 这个测试不读代码，直接用真实 exe 查询三种年龄的闸门。
 *
 * 用法：node tools/test-halt-latch.mjs
 */

import { writeFileSync, utimesSync, rmSync, mkdtempSync, mkdirSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { tempHelper, assertNotLow } from './fixture.mjs'

const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const dir = mkdtempSync(join(tmpdir(), 'dsc-halt-'))
const gate = join(dir, 'halt')

/** 查一次闸门状态；env 覆盖 TTL。 */
const query = (env = {}) => {
  const r = spawnSync(HELPER, ['stop'], {
    encoding: 'utf8', windowsHide: true, timeout: 15000,
    env: { ...process.env, DSC_HALT_GATE: gate, ...env },
  })
  try { return JSON.parse(r.stdout) } catch { return { ok: false, raw: String(r.stdout || r.stderr).slice(0, 120) } }
}

/** 造一份 ageSec 秒前的闸门。 */
const makeGate = (ageSec) => {
  writeFileSync(gate, 'test')
  const t = Date.now() / 1000 - ageSec
  utimesSync(gate, t, t)
}

try {
  console.log()
  console.log('── 1. 70 秒前的闸门：默认必须仍然阻断 ──')
  makeGate(70)
  const r70 = query()
  if (r70.halted === true) ok('70 秒后仍是 halted（锁存）')
  else bad(`70 秒后被当成解除：${JSON.stringify(r70).slice(0, 120)}`)

  console.log()
  console.log('── 2. 一小时前的闸门：仍然阻断 ──')
  makeGate(3600)
  const r1h = query()
  if (r1h.halted === true) ok('3600 秒后仍是 halted')
  else bad(`3600 秒后被当成解除：${JSON.stringify(r1h).slice(0, 120)}`)

  console.log()
  console.log('── 3. 显式设置 TTL 时仍然按配置生效（保留逃生口）──')
  makeGate(70)
  const rTtl = query({ DSC_HALT_TTL_SECONDS: '10' })
  if (rTtl.halted === false) ok('DSC_HALT_TTL_SECONDS=10 时，70 秒的闸门按配置过期')
  else bad(`TTL 覆盖没生效：${JSON.stringify(rTtl).slice(0, 120)}`)

  console.log()
  console.log('── 4. 没有闸门文件 = 未拉下 ──')
  try { rmSync(gate, { force: true }) } catch { }
  const rNone = query()
  if (rNone.halted === false) ok('没有闸门时不阻断')
  else bad(`没有闸门却报 halted：${JSON.stringify(rNone).slice(0, 120)}`)
} finally {
  try { rmSync(dir, { recursive: true, force: true }) } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
