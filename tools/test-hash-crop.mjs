#!/usr/bin/env node
/* 指纹的**纯文件**测试：大图里的一块，和同一块单独存成 PNG，必须算出同一个指纹。
 *
 * 为什么必须有这一条（外部评审 §6）：
 *   之前的"同像素不同指纹"事故里，我拿真实窗口 + 屏幕抓取去比，变量太多
 *   （窗口会不会动、有没有遮挡、cursor 在不在里面、我到底跑的是哪个二进制）。
 *   而它的本质只是"从大图取子矩形"这一步 —— 完全可以离线验，不需要桌面。
 *
 * 覆盖：非整齐宽度（1/3/7/17/171）、非零 x/y、32bppArgb 与 24bppRgb、
 *      以及"整图 vs 同一张图的整图区域"这种退化情形。
 *
 * 用法：node tools/test-hash-crop.mjs
 */

import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
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
const dir = mkdtempSync(join(process.cwd(), '.cache', 'hash-crop-'))

const hash = (args) => {
  const r = spawnSync(HELPER, args, { encoding: 'utf8', windowsHide: true, timeout: 60000 })
  try { return JSON.parse(r.stdout) } catch { return { ok: false, error: 'parse' } }
}

try {
  const gen = spawnSync(PS, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', 'tools/fixtures/make-crop-fixtures.ps1', '-OutDir', dir],
  { encoding: 'utf8', windowsHide: true, timeout: 120000 })
  if (gen.status !== 0) {
    console.log(`  ✗ 生成夹具失败：${String(gen.stderr ?? gen.stdout).slice(0, 200)}`)
    process.exit(1)
  }

  const rows = String(gen.stdout).trim().split('\n').map((l) => {
    const [name, big, crop, x, y, w, h] = l.trim().split('|')
    return { name, big, crop, x: +x, y: +y, w: +w, h: +h }
  })
  console.log()
  console.log(`  生成 ${rows.length} 组夹具（宽度：${rows.map((r) => r.w).join(', ')}）`)
  console.log()

  for (const r of rows) {
    const a = hash(['image_hash', r.big, String(r.x), String(r.y), String(r.w), String(r.h)])
    const b = hash(['image_hash', r.crop, '0', '0', String(r.w), String(r.h)])
    if (!a.ok || !b.ok) {
      bad(`${r.name}: 命令失败 ${JSON.stringify(a).slice(0, 80)} / ${JSON.stringify(b).slice(0, 80)}`)
      continue
    }
    if (a.hash === b.hash) ok(`${r.name}: ${r.w}x${r.h} @(${r.x},${r.y}) 两种取法一致 ${a.hash}`)
    else bad(`${r.name}: 不一致！大图裁出 ${a.hash} ≠ 独立 PNG ${b.hash}（${r.w}x${r.h} @${r.x},${r.y}）`)
  }

  console.log()
  console.log('── 退化情形：整图取整图 ──')
  {
    const r = rows[0]
    const a = hash(['image_hash', r.big, '0', '0', '400', '200'])
    const b = hash(['image_hash', r.big, '0', '0', '400', '200'])
    if (a.ok && a.hash === b.hash) ok('同一张图同样区域，两次一致')
    else bad('同一张图同样区域两次不一致')
  }

  console.log()
  console.log('── 越界仍然要拒 ──')
  {
    const r = rows[0]
    const a = hash(['image_hash', r.big, String(400 - 2), String(200 - 2), '10', '10'])
    if (!a.ok) ok('越界被拒绝')
    else bad(`越界没有拒绝：${JSON.stringify(a).slice(0, 80)}`)
  }
} finally {
  rmSync(dir, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
