#!/usr/bin/env node
/* 阈值扫描：容差取多少，是**测**出来的，不是猜的。
 *
 * 外部评审要求："先建立同一目标的两类样本：语义未变和必须拒绝；把阈值扫描当作
 * 离线实验，例如单像素阈值 0 / 0.02 / 0.05 / 0.1，并记录误拒和漏拒。"
 *
 * 我们比的是"每通道最大差值"，所以这里的阈值是 0..N 的整数通道差，不是 0~1 的比例。
 *
 * 做法：PowerShell 生成确定性样本对（基准图 + 变体），Ground truth 由构造决定：
 *   PASS   = 只有渲染噪声（±1、±2、单列 +1 的接缝）
 *   REJECT = 真的变了（改字、整体位移 2px、遮挡条、整体变暗 8、多出一个勾）
 * 然后对每个容差数一遍：有多少 PASS 被误拒、多少 REJECT 被漏放。
 *
 * 用法：node tools/test-tolerance-sweep.mjs
 */

import { mkdirSync, mkdtempSync, rmSync, readdirSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { tempHelper, assertNotLow } from './fixture.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const PS = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const HELPER = tempHelper()
assertNotLow(HELPER)

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const dir = mkdtempSync(join(process.cwd(), '.cache', 'tolerance-'))

const run = (args) => {
  const r = spawnSync(HELPER, args, {
    encoding: 'utf8', windowsHide: true, timeout: 60000,
    env: { ...process.env, DSC_ALLOWED_APPS: '' },
  })
  try { return JSON.parse(r.stdout) } catch { return { ok: false, error: 'parse' } }
}

try {
  const gen = spawnSync(PS, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', 'tools/fixtures/make-tolerance-samples.ps1', '-OutDir', dir],
  { encoding: 'utf8', windowsHide: true, timeout: 180000 })
  if (gen.status !== 0) {
    console.log(`  ✗ 生成样本失败：${String(gen.stderr ?? gen.stdout).slice(0, 300)}`)
    process.exit(1)
  }

  const samples = String(gen.stdout ?? '').trim().split('\n').map((l) => {
    const [name, path, kind] = l.trim().split('|')
    return { name, path, kind }
  }).filter((s) => s.kind === 'pass' || s.kind === 'reject')

  const base = String(gen.stdout ?? '').trim().split('\n')
    .map((l) => l.trim().split('|')).find((p) => p[2] === 'base')
  if (!base) {
    console.log('  ✗ 没生成基准图')
    process.exit(1)
  }

  const W = 300
  const H = 80
  const tolerances = [0, 1, 2, 3, 4, 6, 8, 12, 20]

  console.log()
  console.log(`  样本：${samples.length} 个变体（pass ${samples.filter((s) => s.kind === 'pass').length}，` +
    `reject ${samples.filter((s) => s.kind === 'reject').length}）`)
  console.log()
  console.log('  容差 | 误拒（PASS 被判变） | 漏放（REJECT 被判没变） | PASS 最大差异像素 | REJECT 最小差异像素')
  console.log('  -----+---------------------+-----------------------+-------------------+-------------------')

  const rows = []
  for (const tol of tolerances) {
    let falseReject = 0
    let falseAccept = 0
    let passMax = 0
    let rejectMin = Infinity
    for (const s of samples) {
      const d = run(['image_diff', base[1], s.path, '0', '0', String(W), String(H), String(tol)])
      if (!d.ok) {
        bad(`image_diff 失败：${s.name} ${d.error ?? ''}`)
        continue
      }
      if (s.kind === 'pass') {
        passMax = Math.max(passMax, d.diffPixels)
        if (d.diffPixels > 0) falseReject++
      } else {
        rejectMin = Math.min(rejectMin, d.diffPixels)
        if (d.diffPixels === 0) falseAccept++
      }
    }
    rows.push({ tol, falseReject, falseAccept, passMax, rejectMin })
    console.log(
      `  ${String(tol).padStart(4)} | ${String(falseReject).padStart(19)} | ` +
      `${String(falseAccept).padStart(21)} | ${String(passMax).padStart(17)} | ${String(rejectMin).padStart(17)}`,
    )
  }

  console.log()
  const clean = rows.filter((r) => r.falseReject === 0 && r.falseAccept === 0)
  console.log(`  误拒与漏放同时为 0 的容差：${clean.length ? clean.map((r) => r.tol).join(', ') : '（没有）'}`)

  // 断言 1：容差 0 一定误拒（噪声/接缝）—— 这解释了为什么当初纯等价比不了
  const zero = rows.find((r) => r.tol === 0)
  if (zero && zero.falseReject > 0) ok(`容差 0 会误拒 ${zero.falseReject} 个"只有噪声"的样本（证明纯等价比不了）`)
  else bad('容差 0 竟然没有误拒 —— 样本或比较逻辑有问题')

  // 断言 2：我们出厂用的容差 2 必须 0 漏放
  const shipped = rows.find((r) => r.tol === 2)
  if (shipped && shipped.falseAccept === 0) ok('出厂容差 2：没有任何真实变化被漏放')
  else bad(`出厂容差 2 漏放了 ${shipped?.falseAccept} 个真实变化`)

  // 断言 3：容差 2 的误拒情况要如实报出来（哪怕不是 0）
  if (shipped && shipped.falseReject === 0) ok('出厂容差 2：噪声样本全部放行')
  else console.log(`  · 出厂容差 2 仍会误拒 ${shipped?.falseReject} 个噪声样本（±3 级别）—— 这是取舍，写进文档`)

  // 断言 4：存在"两边都 0"的区间，说明这套阈值方法是可行的
  if (clean.length > 0) ok(`存在安全区间：容差 ${clean[0].tol}..${clean[clean.length - 1].tol}`)
  else bad('不存在同时无误拒无漏放的容差 —— 需要换比较方法（比如分区/加权），不能只调阈值')
} finally {
  rmSync(dir, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
