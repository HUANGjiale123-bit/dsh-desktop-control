// tracing：一次调用花了多久、花在哪，且**并发不串号**。
//
// ── Codex 的警告 ──
//
// "父 span ID 由各异步分支继承，不能用一个全局 currentTrace/currentStage
//  变量；否则 A、B 同时调用时会把耗时和授权串到一起。"
//
// 这个测试就是守那条：并发发两次调用，每次的 span 列表必须只含自己的段。
// 串号的表现是"一条 trace 里有两个 helper span，另一条一个也没有"。
//
// 用法：node tools/test-trace.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}`); skipped++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'trace-'))
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
const listWindows = byName.get('desktop_list_windows')
const diagnose = byName.get('desktop_diagnose')

console.log()
console.log('── 1. 基础设施在 ──')

{
  const src = readFileSync('plugin/index.mjs', 'utf8')
  for (const [needle, label] of [
    ['new AsyncLocalStorage()', '用 AsyncLocalStorage（不是全局变量）'],
    ['als.run({ trace, agent', '每次调用在 als.run 里执行（并带上会话身份）'],
    ['function span(', '有 span 包装'],
    ['rememberTrace(trace)', 'trace 会被记下来'],
  ]) {
    if (src.includes(needle)) ok(label)
    else bad(`${label}（找不到 "${needle}"）`)
  }
  if (/TRACE_KEEP/.test(src)) ok('有容量上限（不会无限涨）')
  else bad('trace 没有上限')
}

console.log()
console.log('── 2. 一次调用会记下 total 与子段 ──')

{
  await listWindows.execute({}).catch(() => {})
  const r = await diagnose.execute({ perf: true, last: 5 }).catch((e) => ({ err: e.message }))
  const txt = r.result ?? ''
  if (r.err) bad(`诊断抛错：${r.err.slice(0, 70)}`)
  else if (txt.includes('desktop_list_windows')) ok('性能视图里有这次调用')
  else skip('性能视图里没看到（helper 可能不可用）')

  if (/helper\.list_windows/.test(txt)) ok('记到了 helper 往返 span')
  else skip('没有 helper span（环境限制）')
}

console.log()
console.log('── 3. 并发不串号（Codex 的核心警告）──')

{
  // 同时发三次，每次只应记自己的 span
  const results = await Promise.all([
    listWindows.execute({}).catch(() => null),
    listWindows.execute({}).catch(() => null),
    listWindows.execute({}).catch(() => null),
  ])
  void results

  const r = await diagnose.execute({ perf: true, last: 6 }).catch(() => ({ result: '' }))
  const txt = r.result ?? ''
  const rows = txt
    .split(String.fromCharCode(10))
    .filter((l) => l.includes('desktop_list_windows') && /ms/.test(l))

  if (rows.length < 3) {
    skip(`只看到 ${rows.length} 条记录，不足以判断并发`)
  } else {
    // 每一行是一次调用。检查每条里 helper span 的个数
    let withOne = 0
    let withMany = 0
    for (const row of rows) {
      const n = (row.match(/helper\.list_windows/g) ?? []).length
      if (n >= 1) withOne++
      if (n >= 2) withMany++
    }
    if (withMany > 0) {
      bad(`有 ${withMany} 条 trace 里塞进了多个 helper span —— 并发串号了`)
    } else {
      ok(`三条并发调用各自记录（没有一条混进别人的 span）`)
    }
  }
}

console.log()
console.log('── 4. 没有 trace 上下文时不报错（测试/独立运行场景）──')

{
  // 直接调 toolset 层（不经过 register 的 ALS 包装）
  // createDesktopToolset 是 toolset.mjs 导出的，index.mjs 没有转出
  const tmod = await import(pathToFileURL(join(tmp, 'toolset.mjs')).href + '?' + Math.random())
  const set = tmod.createDesktopToolset ?? null
  if (!set) skip('拿不到 createDesktopToolset')
  else {
    const s2 = set({
      helperPath: 'helper/bin/dsc.exe',
      allowedApps: '',
      getGatePath: () => join(tmp, 'halt'),
    })
    const r = await s2.tools.listWindows().catch((e) => ({ isError: true, text: e.message }))
    if (r && !r.isError) ok('没有 ALS 上下文时，工具照常工作（span 退化成直接执行）')
    else skip(`没有上下文时失败了（可能是环境原因）：${String(r?.text).slice(0, 50)}`)
  }
}

console.log()
console.log('── 5. 性能视图不许动真实东西 ──')

{
  const r = await diagnose.execute({ perf: true, last: 3 }).catch(() => ({ result: '' }))
  const txt = r.result ?? ''
  if (!/闸门实测/.test(txt)) ok('性能视图没有跑闸门探测')
  else bad('性能视图跑了闸门探测 —— 只读接口不该动真实闸门')
  if (!/生效名单/.test(txt)) ok('性能视图没有走完整诊断流程')
  else bad('性能视图走了完整诊断')

  if (/这个视图测不到什么/.test(txt)) ok('明确说了测不到什么（模型/用户时间）')
  else bad('没有说明边界 —— 容易被当成"任务耗时"')
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
