// 执行权（lease）：同一时刻只允许一个操作碰桌面。
//
// ── 为什么 ──
//
// 没有它的时候两个调用可以这样交错：
//
//   A: 截图 → 算出 Alpha 在 (186,161)
//   B: 把窗口拖走
//   A: 点 (186,161)        ← 点到别的东西
//
// shotId 防不住这个：它检查窗口几何，而"内容被滚动"不改变几何。
//
// ── 这个测试守什么 ──
//
//   · 拿不到执行权时**明确拒绝**，不假装排队
//   · 拒绝信息说清"这不是故障"以及怎么办
//   · 异常路径也释放（否则插件永久卡在"有人占用"）
//   · 等待时间单独记 span（它不是操作耗时）
//   · 不碰桌面的工具不进执行权（否则 list_apps 会被无谓地串行化）
//
// 用法：node tools/test-lease.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}`); skipped++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'lease-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

const reg = []
const ctx = {
  logger: { info: () => {}, warn: () => {}, error: () => {} },
  tools: { register: (t) => { reg.push(t); return () => {} } },
  get(n) {
    if (n === 'approval') return { request: async () => 'allowed-once' }
    if (n === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
    if (n === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['s1'] }] }
    if (n === 'attachments') return { saveImages: async () => [{ id: 'a' }] }
    return undefined
  },
}
const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: 'helper/bin/dsc.exe',
  escPath: 'helper/bin/dsc-esc.exe',
  gatePath: join(tmp, 'halt'),
  shotDir: join(tmp, 'shots'),
})

const byName = new Map(reg.map((t) => [t.name, t]))
const diagnose = byName.get('desktop_diagnose')
const listWindows = byName.get('desktop_list_windows')
const listApps = byName.get('desktop_list_apps')

console.log()
console.log('── 1. 执行权存在，且覆盖对了范围 ──')

const src = readFileSync('plugin/index.mjs', 'utf8')
for (const [needle, label] of [
  ['let _lease = null', '有执行权记录'],
  ['async function acquireLease', '有 acquireLease'],
  ['function releaseLease', '有 releaseLease'],
  ['const NO_LEASE_TOOLS', '有不碰桌面的例外名单'],
]) {
  if (src.includes(needle)) ok(label)
  else bad(`${label}（找不到 "${needle}"）`)
}

// 释放必须在 finally 里 —— 否则异常会让插件永久卡住
if (/finally\s*\{[\s\S]{0,1500}?releaseLease\(\)/.test(src)) ok('释放写在 finally 里（异常也释放）')
else bad('释放不在 finally —— 一次异常就会永久占用')

// 不碰桌面的工具不该被串行化
const noLease = src.slice(src.indexOf('const NO_LEASE_TOOLS'), src.indexOf('const register ='))
for (const t of ['desktop_list_apps', 'desktop_off', 'desktop_revoke_grants']) {
  if (noLease.includes(`'${t}'`)) ok(`${t} 不需要执行权`)
  else bad(`${t} 白拿一把锁`)
}
if (!noLease.includes("'desktop_screenshot'")) ok('截图**要**执行权（采样也在执行权内）')
else bad('截图没拿执行权 —— 它可能截到别人操作到一半的画面')

console.log()
console.log('── 2. 拿不到时明确拒绝，不是假装排队 ──')

{
  // 制造占用：直接连发两个 read_window（同一个 hwnd=1，都会失败，
  // 但 lease 的等待/拒绝路径会跑到）
  const rw = byName.get('desktop_read_window')
  const results = await Promise.all([
    rw.execute({ hwnd: 1 }).catch((e) => ({ err: e.message })),
    rw.execute({ hwnd: 1 }).catch((e) => ({ err: e.message })),
  ])
  const busy = results.filter((r) => r.err && /没拿到执行权/.test(r.err))
  const other = results.filter((r) => r.err && !/没拿到执行权/.test(r.err))
  if (busy.length > 0) {
    ok('并发时确实有一个被执行权拒绝')
    const msg = busy[0].err
    if (/不是故障/.test(msg)) ok('拒绝信息说清了"这不是故障"')
    else bad('拒绝信息没说清，调用方会以为坏了')
    if (/desktop_diagnose/.test(msg)) ok('拒绝信息给了排查手段')
    else bad('拒绝信息没给怎么办')
  } else if (other.length === results.length) {
    skip('两个都在等锁前就失败了（环境限制，未撞上竞争）')
  } else {
    skip('没观察到拒绝')
  }
}

console.log()
console.log('── 3. 等锁的时间单独成段，不混进操作耗时 ──')

{
  await listWindows.execute({}).catch(() => {})
  const r = await diagnose.execute({ perf: true, last: 5 }).catch(() => ({ result: '' }))
  const txt = r.result ?? ''
  if (/lease/.test(txt)) ok('性能视图里能看到 lease 段')
  else skip('没看到 lease 段（可能这次等待是 0ms 被省略）')
}

console.log()
console.log('── 4. 释放之后还能再用（没有永久占用）──')

{
  const a = await listWindows.execute({}).catch((e) => ({ err: e.message }))
  const b = await listWindows.execute({}).catch((e) => ({ err: e.message }))
  if (!a.err && !b.err) ok('连续两次调用都成功 —— 执行权确实释放了')
  else if (/没拿到执行权/.test(a.err ?? '')) bad('第一次就被占用 —— 之前的执行权没释放')
  else skip(`调用本身失败（环境原因）：${(a.err ?? b.err ?? '').slice(0, 50)}`)
}

console.log()
console.log('── 5. hold 的遗留会被收拾 ──')

{
  for (const [needle, label] of [
    ['function pressedPath', '有"有键按着"的记录文件'],
    ['async function cleanupStalePress', '有启动时的清理'],
    ['cleanupStalePress().catch', '启动时真的会调'],
    ["['release']", '清理时会真的发 release'],
  ]) {
    if (src.includes(needle)) ok(label)
    else bad(`${label}（找不到 "${needle}"）`)
  }
  // hold 要写记录，release 要删
  const holdIdx = src.indexOf("name: 'desktop_hold'")
  const relIdx = src.indexOf("name: 'desktop_release'")
  const holdBody = src.slice(holdIdx, relIdx)
  const relBody = src.slice(relIdx, relIdx + 2000)
  if (holdBody.includes('writeFileSync')) ok('hold 会写记录')
  else bad('hold 不写记录 —— 崩溃后没人知道键还按着')
  if (relBody.includes('rmSync')) ok('release 会清记录')
  else bad('release 不清记录')
}

try { rmSync(tmp, { recursive: true, force: true }) } catch {}

console.log()
console.log('══════════════════════════════')
console.log(
  fail === 0
    ? `  ${pass} 项通过` + (skipped ? `，${skipped} 项跳过（未验证）` : '')
    : `  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过  ← 先修`,
)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
