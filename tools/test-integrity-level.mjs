#!/usr/bin/env node
/* helper 必须能说出自己跑在什么完整性级别上。
 *
 * ── 这条为什么重要 ──
 *
 * 实测（2026-10-19）：同一份 dsc.exe，
 *   放在 DSH 的工作区里（`D:\dsh workspace` 带 Low 标签）→ 进程是 Low，
 *     读 SuYou 只有 7 个节点（Chromium 只给外壳）；
 *   同样字节复制到 %TEMP% → 进程是 Medium，读到 20 个以上。
 *
 * 之前查了很久"是不是启动链 / 是不是 .NET 父进程"，因为 dsc.exe **不说**
 * 自己是什么级别。它现在说了（integrity / integrityRid）。
 *
 * 另外这一格还挡住一个真实 bug：最初的实现用 OpenProcess(自己 pid) 取令牌，
 * 在 Low 下那一路恒失败，于是"级别低"被报成"未知"——正好把唯一的线索掐掉。
 *
 * 用法：node tools/test-integrity-level.mjs
 */

import { spawnSync } from 'node:child_process'
import { copyFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`) }

const HELPER = 'helper/bin/dsc.exe'

function run(exe, args) {
  const r = spawnSync(exe, args, { encoding: 'utf8', windowsHide: true, timeout: 60000 })
  try { return JSON.parse(String(r.stdout || '{}')) } catch { return { ok: false, error: 'parse' } }
}

console.log()
console.log('── 1. ping 必须报出完整级别，而不是"未知" ──')
const ping = run(HELPER, ['ping'])
if (!ping.ok) {
  bad(`ping 失败：${ping.error}`)
} else {
  const name = String(ping.integrity ?? '')
  const rid = Number(ping.integrityRid)
  if (name && name !== '未知' && name !== 'unknown') ok(`ping 报出 integrity=${name}`)
  else bad(`ping 报的是 ${JSON.stringify(name)} —— 读令牌那条路失败了，现场唯一的线索会被掐掉`)

  if (Number.isFinite(rid) && rid > 0) ok(`integrityRid=${rid}`)
  else bad(`integrityRid=${JSON.stringify(ping.integrityRid)}，期望一个正数（4096=Low, 8192=Medium）`)
}

console.log()
console.log('── 2. 工作区里的副本 vs 临时目录里的副本 ──')
const repoTree = run(HELPER, ['tree', process.env.DSC_TEST_HWND || '66988', '40', '--allow', 'su-you.exe'])
const repoName = String(repoTree.integrity ?? '')
const repoTolal = repoTree.total

const dir = mkdtempSync(join(tmpdir(), 'dsc-int-'))
const copy = join(dir, 'dsc.exe')
try {
  copyFileSync(HELPER, copy)
  const outTree = run(copy, ['tree', process.env.DSC_TEST_HWND || '66988', '40', '--allow', 'su-you.exe'])
  const outName = String(outTree.integrity ?? '')

  console.log(`    工作区副本: integrity=${repoName} total=${repoTolal}`)
  console.log(`    临时目录副本: integrity=${outName} total=${outTree.total}`)

  if (repoName === outName) {
    // 在普通机器上（目录没有 Low 标签）两份都是 Medium —— 这条对比无从验证。
    skip('两份副本的完整性级别相同，说明本机没有给工作区打 Low 标签；对比无从验证')
  } else {
    ok(`同一份二进制在不同目录跑出不同级别：${repoName} vs ${outName}`)
    const low = repoName === 'Low' ? repoTolal : outTree.total
    const high = repoName === 'Low' ? outTree.total : repoTolal
    if (Number(low) < Number(high)) {
      ok(`低完整性那份读得更少（${low} < ${high}）—— 与"级别决定看得到多少"一致`)
    } else {
      bad(`低完整性那份反而读到 ${low} 个（高完整性 ${high} 个）—— 与结论不符，需要重查`)
    }
  }
} finally {
  rmSync(dir, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
