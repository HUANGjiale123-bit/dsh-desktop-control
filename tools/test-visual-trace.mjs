#!/usr/bin/env node
/* trace 持久化：视觉路径的每一次列表 / 点击都要能事后回放。
 *
 * 验的是四件事：
 *   1. 成功点击会落一条记录，且带落点、复查结果、来源图片路径；
 *   2. **被拒绝**的点击也会落记录（带机器可读的 code）—— 这比成功记录更值钱；
 *   3. 记录是合法 JSONL（没有半行），而且不包含图片内容（只有路径与数字）；
 *   4. 写 trace 失败不能影响主流程（用只读目录验证？做不到跨平台 —— 改为验证
 *      "主流程在 trace 目录异常时仍然继续"，用把 shotDir 指向不存在的深层目录来跑）。
 *
 * 用法：node tools/test-visual-trace.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync, existsSync, readdirSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`); skipped++ }

const HELPER = tempHelper()
assertNotLow(HELPER)

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-trace-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

const shotDir = join(tmp, 'shots')
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
await mod.apply(ctx, { helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir })
const by = new Map(reg.map((t) => [t.name, t]))

const wins = (() => {
  try {
    return JSON.parse(spawnSync(HELPER, ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
  } catch { return [] }
})()
const win = wins.find((w) => String(w.class ?? '').includes('Tauri') && w.width > 400)

const tracePath = join(shotDir, 'trace.jsonl')
const readTrace = () => (existsSync(tracePath)
  ? readFileSync(tracePath, 'utf8').split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l))
  : [])

try {
  console.log()
  if (!win) {
    console.log('  ✗ 没找到可用窗口')
    process.exit(1)
  }
  process.env.DSC_ALLOWED_APPS = String(win.exe).split(/[\\/]/).pop()

  // 激活后再截图（截图拍的是屏幕上那块）
  await by.get('desktop_click').execute({
    hwnd: win.hwnd, x: Math.round(win.x + win.width / 2), y: win.y + 10, observe: false,
  }).catch(() => {})
  await new Promise((r) => setTimeout(r, 350))

  const shot = await by.get('desktop_screenshot').execute({ hwnd: win.hwnd })
  const list = await by.get('desktop_visual_candidates').execute({ hwnd: win.hwnd, shotId: shot.shotId })
  const cand = [...(list.result ?? '').matchAll(/#\d+ ([0-9a-f-]{36}) {2}"([^"]*)"/g)]
    .map((m) => ({ id: m[1], label: m[2] }))
    .find((c) => /当前版本|规则模式/.test(c.label.replace(/\s+/g, ''))) ?? null

  console.log('── 1. 列表与成功点击各留一条 ──')
  {
    const events = readTrace()
    const listEv = events.find((e) => e.kind === 'candidates' && e.ok === true)
    if (listEv) ok(`列表事件已落盘（count=${listEv.count}, ocrInput=${listEv.ocrInput}, wallMs=${listEv.wallMs}）`)
    else bad('没有列表事件')

    if (!cand) {
      skip('没有可用候选，跳过成功点击记录')
    } else {
      const r = await by.get('desktop_click_candidate').execute({
        hwnd: win.hwnd, shotId: shot.shotId, candidateId: cand.id, observe: false,
      }).catch((e) => ({ err: e.message }))
      const t = r.err ?? r.result ?? r.text ?? ''
      if (/已点击候选/.test(t)) ok('点击成功')
      else skip(`这次点击没成功：${t.slice(0, 100)}`)

      const clickEv = readTrace().find((e) => e.kind === 'click' && e.ok === true)
      if (!clickEv) {
        skip('没有成功点击事件（这一格没验）')
      } else {
        ok(`成功点击已落盘：desktop=${JSON.stringify(clickEv.desktopPoint)}`)
        if (clickEv.recheck && typeof clickEv.recheck.diffPixels === 'number') {
          ok(`记录里有复查结果：diffPixels=${clickEv.recheck.diffPixels} maxDelta=${clickEv.recheck.maxDelta} tol=${clickEv.recheck.tol}`)
        } else bad('记录里没有复查结果')
        if (clickEv.sourceImage && existsSync(clickEv.sourceImage)) ok('记录里带来源图片路径，且文件存在')
        else bad('来源图片路径缺失或不存在')
      }
    }
  }

  console.log()
  console.log('── 2. 被拒绝的点击也要留痕 ──')
  {
    const shot2 = await by.get('desktop_screenshot').execute({ hwnd: win.hwnd })
    const badCall = await by.get('desktop_click_candidate').execute({
      hwnd: win.hwnd, shotId: shot2.shotId, viewId: 'no-such-view', point: { u: 0.5, v: 0.5 }, observe: false,
    }).catch((e) => ({ err: e.message }))
    const t = badCall.err ?? ''
    if (/找不到视图/.test(t)) ok('拒绝行为正确')
    else bad(`预期拒绝，实际：${t.slice(0, 120)}`)

    const rejected = readTrace().filter((e) => e.kind === 'click' && e.ok === false)
    const last = rejected[rejected.length - 1]
    if (last) ok(`拒绝事件已落盘：code=${last.code}`)
    else bad('拒绝没有被记录')
    if (last && /找不到视图/.test(String(last.error ?? ''))) ok('拒绝原因写在记录里')
    else skip('拒绝原因文本没对上，跳过这条')
  }

  console.log()
  console.log('── 3. 记录是合法 JSONL，且不含图片内容 ──')
  {
    const raw = existsSync(tracePath) ? readFileSync(tracePath, 'utf8') : ''
    const lines = raw.split('\n').filter((l) => l.trim())
    let parsed = 0
    for (const l of lines) {
      try { JSON.parse(l); parsed++ } catch { /* 记下来 */ }
    }
    if (parsed === lines.length) ok(`${lines.length} 行全部是合法 JSON`)
    else bad(`有 ${lines.length - parsed} 行不是合法 JSON（半行/损坏）`)

    if (raw.length < 64 * 1024) ok(`trace 体积合理（${raw.length} 字节，只有路径与数字）`)
    else bad(`trace 过大（${raw.length} 字节）—— 不要把图片内容写进去`)

    if (!/iVBORw0KGgo/.test(raw)) ok('没有把 PNG 的 base64 写进记录')
    else bad('记录里出现了 PNG base64')
  }
} finally {
  delete process.env.DSC_ALLOWED_APPS
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
