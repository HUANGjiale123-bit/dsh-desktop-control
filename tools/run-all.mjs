#!/usr/bin/env node
/* 跑全部测试，并把**每项的通过/失败/跳过**汇总出来。
 *
 * 为什么要单独一个 runner：外部评审指出"不能仅以退出码 0 汇总为功能验收完成"。
 * 有些测试在环境不满足时会 skip（窗口被移动、没有第二台机器、夹具起不来），
 * 而退出码是 0 —— 只看那个数字会以为"全都验过了"。
 *
 * 用法：
 *   node tools/run-all.mjs            # 全部
 *   node tools/run-all.mjs visual     # 只跑文件名含 visual 的
 *
 * 退出码：有失败 → 1；否则 0（跳过不算失败，但会显式列出来）。
 */

import { readdirSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'

const filter = process.argv[2] ?? ''
const files = readdirSync('tools')
  .filter((f) => /^test-.*\.mjs$/.test(f))
  .filter((f) => !filter || f.includes(filter))
  .sort()

const rows = []
let totalPass = 0
let totalFail = 0
let totalSkip = 0
let crashed = []

for (const f of files) {
  const started = Date.now()
  const r = spawnSync(process.execPath, [join('tools', f)], {
    encoding: 'utf8', timeout: 15 * 60 * 1000, windowsHide: true,
  })
  const ms = Date.now() - started
  const out = String(r.stdout ?? '') + String(r.stderr ?? '')

  // 每个测试最后一行形如：`N 项通过，M 项失败，K 项跳过`（跳过可能缺省）
  // 汇总行有好几种写法，全部认：
  //   "N 项通过，M 项失败，K 项跳过"
  //   "全部通过（22 项）"
  //   "N 项通过"
  let pass = 0
  let fail = 0
  let skip = 0
  let matched = false
  const m1 = /(\d+)\s*项通过[，,]\s*(\d+)\s*项失败(?:[，,]\s*(\d+)\s*项跳过)?/.exec(out)
  if (m1) {
    pass = Number(m1[1]); fail = Number(m1[2]); skip = Number(m1[3] ?? 0); matched = true
  } else {
    const m2 = /全部通过（(\d+)\s*项）/.exec(out)
    if (m2) { pass = Number(m2[1]); matched = true }
    else {
      const m3 = /(\d+)\s*项通过/.exec(out)
      if (m3) { pass = Number(m3[1]); matched = true }
    }
  }

  // 输出里有 ✗ 但汇总说 0 失败 → 自相矛盾，单独标出来
  const hasCross = /✗/.test(out)
  const inconsistent = hasCross && fail === 0

  const crashedHere = r.status !== 0 && !matched
  if (crashedHere) crashed.push(f)

  rows.push({ f, pass, fail, skip, ms, status: r.status, inconsistent, crashed: crashedHere, matched })
  totalPass += pass
  totalFail += fail
  totalSkip += skip
}

const pad = (s, n) => String(s).padStart(n)
console.log()
console.log('  文件'.padEnd(40) + pad('通过', 6) + pad('失败', 6) + pad('跳过', 6) + pad('耗时', 8) + '  备注')
console.log('  ' + '─'.repeat(78))
for (const r of rows) {
  const note = r.crashed ? '直接崩溃（没有汇总行）'
    : r.inconsistent ? '输出有 ✗ 但汇总说 0 失败'
      : !r.matched ? '没有汇总行（只看了退出码）'
        : ''
  const name = r.f.length > 38 ? r.f.slice(0, 35) + '…' : r.f
  console.log(
    '  ' + name.padEnd(38) +
    pad(r.pass, 6) + pad(r.fail, 6) + pad(r.skip, 6) +
    pad((r.ms / 1000).toFixed(1) + 's', 8) + '  ' + note,
  )
}
console.log('  ' + '─'.repeat(78))
console.log(
  '  ' + '合计'.padEnd(38) +
  pad(totalPass, 6) + pad(totalFail, 6) + pad(totalSkip, 6),
)

const skipped = rows.filter((r) => r.skip > 0)
if (skipped.length) {
  console.log()
  console.log(`  ⚠ ${skipped.length} 个文件里有跳过项（共 ${totalSkip} 项）—— 这些**没有验过**：`)
  for (const r of skipped) console.log(`     · ${r.f}  跳过 ${r.skip} 项`)
}

if (crashed.length) {
  console.log()
  console.log(`  ✗ ${crashed.length} 个文件直接崩溃：${crashed.join(', ')}`)
}

console.log()
const bad = totalFail > 0 || crashed.length > 0
console.log(`  ${bad ? '✗ 有失败' : '✓ 全部通过'}（失败 ${totalFail}，跳过 ${totalSkip}）`)
process.exit(bad ? 1 : 0)
