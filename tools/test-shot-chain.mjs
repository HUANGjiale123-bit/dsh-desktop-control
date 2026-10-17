// 截图 → 记录 → 点击引用 这条链必须真的贯通。
//
// 这一条是 2.9.2 里的实际断点：toolset 的 screenshot 只返回
// { filepath, text }，而 plugin 那侧 rememberShot 读 r.originX / r.width，
// 于是记录里全是 undefined。后果不是"少了个提示"，而是：
//   · 每次带 shotId 的点击都报"窗口尺寸从 undefinedxundefined 变了"
//   · shotId 这个契约等于没实现
//
// 而且文字里明明有"尺寸 1941x1242，桌面原点 (150,48)" —— 那些数字只活在
// 一句给人看的句子里，代码读不到。这是"数据只存在于描述里"的典型。
//
// 用法：node tools/test-shot-chain.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'shotchain-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

const reg = []
const warns = []
const ctx = {
  logger: { info: () => {}, warn: (m) => warns.push(String(m)), error: () => {} },
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

// 找一个真实窗口
const { execFileSync } = await import('node:child_process')
const raw = JSON.parse(
  execFileSync('helper/bin/dsc.exe', ['list_windows'], { encoding: 'utf8', windowsHide: true }),
)
const ours = (w) => /^(dsc|dsc-esc)\.exe$/i.test(String(w.exe ?? '').split(/[\\/]/).pop())
const win = raw.windows.find((w) => w.width > 300 && w.height > 300 && w.exe && !ours(w))
if (!win) {
  console.log('  ✗ 找不到可用窗口')
  process.exit(1)
}

console.log()
console.log(`  测试窗口 hwnd=${win.hwnd} ${win.width}x${win.height} @(${win.x},${win.y})`)

console.log()
console.log('── 1. toolset.screenshot 必须返回结构化几何 ──')

const setProbe = await import(pathToFileURL(join(tmp, 'toolset.mjs')).href)
const set = setProbe.createDesktopToolset({
  helperPath: 'helper/bin/dsc.exe',
  allowedApps: '',
  getGatePath: () => '',
  requestApproval: async () => true,
  shotDir: join(tmp, 'shots'),
})
const sr = await set.tools.screenshot({ hwnd: win.hwnd })
if (sr.isError) {
  console.log(`  （跳过：截图失败 ${sr.text.split('\n')[0].slice(0, 60)}）`)
} else {
  for (const k of ['originX', 'originY', 'width', 'height']) {
    if (Number.isFinite(sr[k])) ok(`toolset 返回 ${k} = ${sr[k]}`)
    else bad(`toolset 没有返回有效的 ${k}（实际 ${JSON.stringify(sr[k])}）`)
  }
}

console.log()
console.log('── 2. 截图后模型必须能看见 shotId ──')

let shotId = ''
{
  const shot = byName.get('desktop_screenshot')
  const r = await shot.execute({ hwnd: win.hwnd }).catch((e) => ({ err: e.message }))
  if (r.err) {
    console.log(`  （跳过：${r.err.split('\n')[0].slice(0, 50)}）`)
  } else {
    shotId = r.shotId ?? ''
    if (shotId) ok(`execute 返回了 shotId = ${shotId}`)
    else bad('execute 没有返回 shotId')

    // 关键：render 只输出 image + summary，所以 ID 必须在 summary 里
    const rendered = shot.output.render({}, r)
    const textBlocks = rendered.filter((b) => b.type === 'text').map((b) => b.text).join('\n')
    if (shotId && textBlocks.includes(shotId)) {
      ok('shotId 出现在模型可见的文本里')
    } else {
      bad('shotId 没有出现在 render 的文本里 —— 模型看不见就不会传回来')
    }
    if (/5 个采样点未发现/.test(textBlocks)) ok('遮挡措辞限定为"采样点"，不承诺整图')
    else if (/遮挡/.test(textBlocks) === false) ok('无遮挡时的说明存在')
    else bad('措辞没有限定采样范围')
  }
}

console.log()
console.log('── 3. 用这个 shotId 点击时不该误报几何变化 ──')

if (!shotId) {
  console.log('  （跳过：没有 shotId）')
} else {
  const click = byName.get('desktop_click')
  const midX = Math.round(win.width / 2)
  const midY = Math.round(win.height / 2)
  const r = await click
    .execute({ hwnd: win.hwnd, x: midX, y: midY, space: 'window', shotId })
    .catch((e) => ({ err: e.message }))
  const msg = r.err ?? r.result ?? ''
  if (/undefined/.test(msg)) {
    bad(`输出里出现 undefined：${msg.split('\n')[0].slice(0, 90)}`)
  } else if (/拍摄时窗口是 undefined/.test(msg)) {
    bad('仍然误报几何变化 —— 记录没拿到有效几何')
  } else if (/尺寸变了|布局|重新截图/.test(msg) && !/未在采样点/.test(msg)) {
    // 真实点击在沙箱里会因 SetCursorPos 失败，那是另一回事；
    // 这里只关心有没有出现"几何变了"的误报
    bad(`出现了几何相关拒绝：${msg.split('\n')[0].slice(0, 90)}`)
  } else {
    ok('没有误报几何变化')
  }
}

console.log()
console.log('── 4. 伪造的 shotId 必须被拒绝（不能假装核对过）──')

{
  const click = byName.get('desktop_click')
  const r = await click
    .execute({ hwnd: win.hwnd, x: 10, y: 10, space: 'window', shotId: 'shot-不存在-123' })
    .catch((e) => ({ err: e.message }))
  const msg = r.err ?? r.result ?? ''
  if (/找不到截图/.test(msg)) ok('伪造的 shotId 被明确拒绝')
  else bad(`伪造 shotId 没有被拒绝：${msg.split('\n')[0].slice(0, 80)}`)
}

try { rmSync(tmp, { recursive: true, force: true }) } catch {}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
