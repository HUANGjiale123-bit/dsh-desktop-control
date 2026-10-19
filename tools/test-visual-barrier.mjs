#!/usr/bin/env node
/* 第 4 条的**行为级**测试：OCR 还在跑的时候观察被作废，迟到的结果不能登记。
 *
 * 外部评审用屏障复现过旧实现会留下悬空候选。现在 OCR 是异步子进程，
 * 竞态是真实存在的，所以这一条可以从"源码级断言"升级成真测试：
 *
 *   1. 截图 S
 *   2. 起一次 OCR（脚本睡 2 秒，用 DSC_OCR_SCRIPT 换成慢脚本）
 *   3. OCR 睡着的时候，用 desktop_click 做一次动作 —— 它会在结束时作废 S
 *   4. 等 OCR 回来：结果必须是"观察已失效，未登记候选"
 *   5. 拿它返回的东西去点击 → 必须找不到候选（证明真的没登记）
 *
 * 不用 sleep 撞运气：慢脚本保证"作废"一定发生在 OCR 返回之前。
 *
 * 用法：node tools/test-visual-barrier.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs'
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
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-barrier-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

/* 慢 OCR：睡 2 秒，然后吐一份形状合法的最小结果。
 * 注意它报的图片尺寸故意和真实 PNG 不一致 —— runOcr 会标 imageVerified:false，
 * 但这条测的不是尺寸，是"作废后不许登记"。 */
const slow = join(tmp, 'slow-ocr.ps1')
writeFileSync(slow, [
  'Start-Sleep -Seconds 2',
  "Write-Output '{\"ok\":true,\"backend\":\"windows-ocr\",\"languages\":[\"zh-Hans-CN\"],\"engineLanguage\":\"zh-Hans-CN\",\"image\":{\"width\":100,\"height\":100},\"elapsedMs\":1,\"lines\":[{\"text\":\"OLD\",\"x\":1,\"y\":1,\"w\":30,\"h\":10,\"words\":[]}]}'",
].join('\n'))

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
process.env.DSC_OCR_SCRIPT = slow
const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, { helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots') })
delete process.env.DSC_OCR_SCRIPT

const by = new Map(reg.map((t) => [t.name, t]))
const shotTool = by.get('desktop_screenshot')
const candTool = by.get('desktop_visual_candidates')
const clickCand = by.get('desktop_click_candidate')
const clickTool = by.get('desktop_click')

const wins = (() => {
  try {
    return JSON.parse(spawnSync(HELPER, ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
  } catch { return [] }
})()
const win = wins.find((w) => String(w.class ?? '').includes('Tauri') && w.width > 400 && w.height > 400)

try {
  console.log()
  if (!win) {
    console.log('  ✗ 没找到可用窗口')
    process.exit(1)
  }
  process.env.DSC_ALLOWED_APPS = String(win.exe).split(/[\\/]/).pop()

  const shot = await shotTool.execute({ hwnd: win.hwnd })
  console.log(`  截图 ${shot.shotId}，接着起一次 2 秒的 OCR`)

  // 不 await：让 OCR 在后台跑
  const pending = candTool.execute({ hwnd: win.hwnd, shotId: shot.shotId })
    .then((r) => ({ ok: true, value: r }))
    .catch((e) => ({ ok: false, error: e.message }))

  // 等 OCR 真的开始（工具进到 await runOcr）
  await new Promise((r) => setTimeout(r, 500))

  console.log()
  console.log('── OCR 睡着的时候，做一次动作把观察作废 ──')
  const clicked = await clickTool.execute({
    hwnd: win.hwnd,
    x: Math.round(win.x + win.width / 2),
    y: win.y + 10,          // 标题栏中间：只激活，不点到按钮
    observe: false,
  }).catch((e) => ({ err: e.message }))
  const ct = clicked.err ?? clicked.result ?? clicked.text ?? ''
  if (/已点击|点击/.test(ct)) ok('作废动作已执行（desktop_click 成功）')
  else skip(`作废动作没成功，这一格没验：${ct.slice(0, 120)}`)

  const late = await pending
  console.log()
  console.log('── OCR 迟到的结果 ──')
  const lt = late.ok ? (late.value.result ?? '') : late.error
  if (/失效|StaleObservation/.test(lt)) ok('迟到的结果被拒绝：观察在 OCR 期间失效')
  else if (/没有登记任何候选/.test(lt)) ok('迟到的结果没有登记候选')
  else bad(`迟到的结果被当成有效了：${lt.slice(0, 200)}`)

  if (/OLD/.test(lt) && !/没有候选/.test(lt)) {
    bad('返回里还夹带了旧文字候选 —— 等于把死观察的内容给了模型')
  } else {
    ok('返回里没有夹带旧文字候选')
  }

  // 真的没登记：拿它给的 (可能的) id 去点，必须找不到
  const id = (/#\d+ ([0-9a-f-]{36})/.exec(lt) ?? [])[1]
  if (!id) {
    ok('连候选 ID 都没有返回（说明压根没登记）')
  } else {
    const r = await clickCand.execute({ hwnd: win.hwnd, shotId: shot.shotId, candidateId: id, observe: false })
      .catch((e) => ({ err: e.message }))
    const t = r.err ?? r.result ?? r.text ?? ''
    if (/找不到候选|StaleObservation|找不到截图/.test(t)) ok('按这个 ID 点击失败（证明没登记）')
    else bad(`悬空候选竟然可点：${t.slice(0, 160)}`)
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
