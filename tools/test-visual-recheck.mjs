#!/usr/bin/env node
/* 候选点击的**内容复查**：确定性验证，不依赖真实应用，也不需要谁保证窗口不动。
 *
 * 场景（自造窗口，它会自己在 2.5 秒后把文字从 ALPHA-ONE 改成 BETA-TWO）：
 *
 *   1. 截图时文字是 ALPHA-ONE；
 *   2. 期间内容变了（窗口没动、shot 没过期、几何完全一致）；
 *   3. 用旧候选去点 → **必须拒绝**，并且一个字节的输入都不发；
 *   4. 重新截图拿新候选（BETA-TWO）→ 应当点得动（正向对照）。
 *
 * 为什么不用 SuYou 这类真实应用做这一格：窗口位置、页面状态、实时数字都会变，
 * 一旦变了，"截图里那块区域"和屏幕上的就不是同一块 —— 测出来的东西没有意义。
 *
 * 用法：node tools/test-visual-recheck.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`); skipped++ }

const PS = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe'
const HELPER = tempHelper()
assertNotLow(HELPER)

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-recheck-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
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
  helperPath: HELPER,
  escPath: 'helper/bin/dsc-esc.exe',
  shotDir: join(tmp, 'shots'),
})

const by = new Map(reg.map((t) => [t.name, t]))
const shotTool = by.get('desktop_screenshot')
const candTool = by.get('desktop_visual_candidates')
const clickCand = by.get('desktop_click_candidate')

function parseCandidates(text) {
  const out = []
  const re = /#\d+ ([0-9a-f-]{36}) {2}"([^"]*)" {2}@\((\d+),(\d+) (\d+)x(\d+)\)/g
  for (const m of String(text).matchAll(re)) {
    out.push({ id: m[1], label: m[2], box: { x: +m[3], y: +m[4], w: +m[5], h: +m[6] } })
  }
  return out
}
const findWin = (title) => {
  try {
    const ws = JSON.parse(spawnSync(HELPER, ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
    return ws.find((w) => String(w.title ?? '').includes(title))
  } catch { return undefined }
}

/* 起一个自己会变脸的窗口。它自己不会动，所以"窗口几何不变"是确定的。 */
const fixture = spawn(PS, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
  '-File', 'tools/fixtures/recheck-window.ps1', '-DelayMs', '2500'],
{ stdio: ['ignore', 'pipe', 'pipe'] })
let fixtureOut = ''
fixture.stdout.on('data', (d) => { fixtureOut += d.toString() })

try {
  console.log()
  let win = null
  for (let i = 0; i < 20 && !win; i++) {
    await new Promise((r) => setTimeout(r, 250))
    win = findWin('dsc-recheck-fixture')
  }
  if (!win) {
    console.log(`  ✗ 造不出测试窗口。输出：${fixtureOut.slice(0, 120)}`)
    process.exit(1)
  }
  console.log(`  靶子 hwnd=${win.hwnd} ${win.width}x${win.height} @(${win.x},${win.y})（它会在 2.5 秒后自己改文字）`)

  // 1. 趁文字还是 ALPHA-ONE 时截图、取候选
  const shot = await shotTool.execute({ hwnd: win.hwnd })
  const shotId = shot.shotId
  if (!shotId) {
    console.log('  ✗ 拿不到 shotId')
    process.exit(1)
  }
  const list = await candTool.execute({ hwnd: win.hwnd, shotId })
  const before = parseCandidates(list.result ?? '')
  const alpha = before.find((c) => /ALPHA/i.test(c.label))

  console.log()
  console.log('── 1. 截图时能拿到旧文字候选 ──')
  if (alpha) ok(`候选里有旧文字：「${alpha.label}」`)
  else bad(`候选里没有 ALPHA-ONE，实际：${before.map((c) => c.label).join(' / ') || '（空）'}`)

  console.log()
  console.log('── 2. 内容变了（窗口没动、shot 没过期）→ 必须拒绝 ──')
  {
    if (!alpha) {
      skip('没有旧文字候选，这一格没验')
    } else {
      await new Promise((r) => setTimeout(r, 3200))   // 等它真的改完
      const now = findWin('dsc-recheck-fixture')
      if (!now || now.x !== win.x || now.y !== win.y) {
        skip(`窗口在这期间被移动了（${now?.x},${now?.y}），这一格没法验`)
      } else {
        const r = await clickCand.execute({ hwnd: win.hwnd, shotId, candidateId: alpha.id, observe: false })
          .catch((e) => ({ err: e.message }))
        const t = r.err ?? r.result ?? r.text ?? ''
        if (/已经变了/.test(t) && /没有发送任何输入/.test(t)) {
          ok('拒绝了：复查发现那块像素变了，没有发送输入')
        } else if (/已点击候选/.test(t)) {
          bad(`内容变了却仍然点下去了：${t.split('\n')[0]}`)
        } else {
          bad(`预期之外：${t.slice(0, 200)}  原始=${JSON.stringify(r).slice(0, 400)}`)
        }
      }
    }
  }

  console.log()
  console.log('── 3. 正向对照：重新截图后，新文字应当点得动 ──')
  {
    const shot2 = await shotTool.execute({ hwnd: win.hwnd })
    const list2 = await candTool.execute({ hwnd: win.hwnd, shotId: shot2.shotId })
    const now = parseCandidates(list2.result ?? '')
    const beta = now.find((c) => /BETA/i.test(c.label)) ?? now.find((c) => /KEEP-STILL/i.test(c.label))
    if (!beta) {
      skip(`新截图里没有 BETA/KEEP-STILL 候选：${now.map((c) => c.label).join(' / ') || '（空）'}`)
    } else {
      const r = await clickCand.execute({
        hwnd: win.hwnd, shotId: shot2.shotId, candidateId: beta.id, observe: false,
      }).catch((e) => ({ err: e.message }))
      const t = r.err ?? r.result ?? r.text ?? ''
      if (/已点击候选/.test(t) && /复查：那块 \d+x\d+ 像素与截图时一致/.test(t)) {
        ok(`新候选点得动（「${beta.label}」），复查通过`)
      } else {
        bad(`正向对照失败：${t.slice(0, 200)}`)
      }
    }
  }
} finally {
  try { fixture.kill() } catch { /* 它自己也会退出 */ }
  await new Promise((r) => setTimeout(r, 400))
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
