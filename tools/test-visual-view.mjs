#!/usr/bin/env node
/* 第二批：裁图放大（desktop_visual_view）+ 模型报点（click_candidate 的 viewId+point）。
 *
 * 两条断言分开看：
 *   · 纯文件：裁图命令必须**像素级正确**（1× 裁出来和原图那块同指纹；2× 尺寸翻倍）；
 *   · 集成：报点 {u,v} 换算到窗口的落点必须落在**那块裁图对应的窗口区域**里，
 *     并且越界/失效的引用一律拒绝。
 *
 * 用法：node tools/test-visual-view.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync, existsSync } from 'node:fs'
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

const PS = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
const HELPER = tempHelper()
assertNotLow(HELPER)

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-view-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

const run = (args, env) => {
  const r = spawnSync(HELPER, args, {
    encoding: 'utf8', windowsHide: true, timeout: 60000,
    env: { ...process.env, ...(env ?? {}) },
  })
  try { return JSON.parse(r.stdout) } catch { return { ok: false, error: 'parse' } }
}

try {
  console.log()
  console.log('── 1. 裁图命令本身（纯文件）──')
  {
    const gen = spawnSync(PS, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-File', 'tools/fixtures/make-crop-fixtures.ps1', '-OutDir', join(tmp, 'img')],
    { encoding: 'utf8', windowsHide: true, timeout: 120000 })
    const rows = String(gen.stdout ?? '').trim().split('\n').filter(Boolean).map((l) => {
      const [name, big, crop, x, y, w, h] = l.trim().split('|')
      return { name, big, crop, x: +x, y: +y, w: +w, h: +h }
    })
    const r = rows.find((r0) => r0.name === 'w171')
    if (!r) {
      skip('没有生成夹具，跳过纯文件部分')
    } else {
      const out1 = join(tmp, 'img', 'c1.png')
      const c1 = run(['crop', r.big, out1, String(r.x), String(r.y), String(r.w), String(r.h), '1'],
        { DSC_ALLOWED_APPS: '' })
      if (c1.ok && c1.width === r.w && c1.height === r.h) ok(`1× 裁图尺寸正确：${c1.width}x${c1.height}`)
      else bad(`1× 裁图失败或尺寸不对：${JSON.stringify(c1).slice(0, 120)}`)

      const hBig = run(['image_hash', r.big, String(r.x), String(r.y), String(r.w), String(r.h)],
        { DSC_ALLOWED_APPS: '' })
      const hCrop = run(['image_hash', out1, '0', '0', String(r.w), String(r.h)], { DSC_ALLOWED_APPS: '' })
      if (hBig.ok && hCrop.ok && hBig.hash === hCrop.hash) ok('1× 裁图与源图那块**像素一致**（同指纹）')
      else bad(`裁图改变了像素：${hBig.hash} vs ${hCrop.hash}`)

      const out2 = join(tmp, 'img', 'c2.png')
      const c2 = run(['crop', r.big, out2, String(r.x), String(r.y), String(r.w), String(r.h), '2'],
        { DSC_ALLOWED_APPS: '' })
      if (c2.ok && c2.width === r.w * 2 && c2.height === r.h * 2) ok(`2× 裁图尺寸翻倍：${c2.width}x${c2.height}`)
      else bad(`2× 裁图尺寸不对：${JSON.stringify(c2).slice(0, 120)}`)

      const over = run(['crop', r.big, join(tmp, 'img', 'bad.png'), '390', '190', '50', '50', '1'],
        { DSC_ALLOWED_APPS: '' })
      if (!over.ok) ok('越界裁图被拒绝')
      else bad('越界裁图没有拒绝')
    }
  }

  console.log()
  console.log('── 2. 视图 + 报点（真实窗口）──')
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
  await mod.apply(ctx, { helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots') })
  const by = new Map(reg.map((t) => [t.name, t]))

  const wins = run(['list_windows'], { DSC_ALLOWED_APPS: '' }).windows ?? []
  const win = wins.find((w) => String(w.class ?? '').includes('Tauri') && w.width > 400 && w.height > 400)
  if (!win) {
    skip('没有可用窗口')
  } else {
    process.env.DSC_ALLOWED_APPS = String(win.exe).split(/[\\/]/).pop()
    const shot = await by.get('desktop_screenshot').execute({ hwnd: win.hwnd })
    // 取底部一条（放大约 2 倍）：688x158 的裁剪
    // 取中段：底部标签栏有 hover/动画，容易把"静态"假设打破。
    const region = { x: 0.1, y: 0.35, w: 0.8, h: 0.12 }
    const view = await by.get('desktop_visual_view').execute({
      hwnd: win.hwnd, shotId: shot.shotId, region, scale: 2,
    })
    const vt = view.result ?? ''
    const viewId = /视图 ([0-9a-f-]{36})/.exec(vt)?.[1]
    if (viewId) ok(`拿到视图 ${viewId.slice(0, 8)}…`)
    else bad(`没有返回 viewId：${vt.slice(0, 160)}`)
    if (view.image) ok('返回了放大后的图像块（模型真的能看到）')
    else bad('没有返回图像块')
    if (/放大 2×/.test(vt)) ok('返回里写明了裁剪与放大倍数')
    else bad(`没写放大倍数：${vt.slice(0, 120)}`)

    const m = /裁剪 \((\d+),(\d+) (\d+)x(\d+)\)/.exec(vt)
    const crop = m ? { x: +m[1], y: +m[2], w: +m[3], h: +m[4] } : null

    // 报点：裁图正中间
    const r = await by.get('desktop_click_candidate').execute({
      hwnd: win.hwnd, shotId: shot.shotId, viewId, point: { u: 0.5, v: 0.5 }, observe: false,
    }).catch((e) => ({ err: e.message }))
    const t = r.err ?? r.result ?? r.text ?? ''
    if (/已点击候选「报点/.test(t)) ok('按报点点击成功（走的是同一套复查+链路）')
    else bad(`报点点击失败：${t.slice(0, 200)}`)

    if (crop) {
      const dm = /桌面 \((\d+),(\d+)\)/.exec(t)
      if (dm) {
        const px = +dm[1]
        const py = +dm[2]
        // 换算回图片内坐标，必须落在裁剪区域内（允许 1~2px 取整误差）
        const ix = px - win.x
        const iy = py - win.y
        const inside = ix >= crop.x - 2 && ix <= crop.x + crop.w + 2 &&
                       iy >= crop.y - 2 && iy <= crop.y + crop.h + 2
        if (inside) ok(`落点 (${ix},${iy}) 在裁剪区 (${crop.x},${crop.y} ${crop.w}x${crop.h}) 内`)
        else bad(`落点 (${ix},${iy}) 跑出了裁剪区 (${crop.x},${crop.y} ${crop.w}x${crop.h})`)
      } else {
        skip('返回里没有落点坐标')
      }
    }

    console.log()
    console.log('── 3. 坏引用一律拒绝 ──')
    // 每段用自己的一对新"观察 + 视图"：上一段点过之后旧观察会被作废，这是设计如此。
    const shot2 = await by.get('desktop_screenshot').execute({ hwnd: win.hwnd })
    const shotId2 = shot2.shotId
    const view2 = await by.get('desktop_visual_view').execute({
      hwnd: win.hwnd, shotId: shotId2, region: { x: 0.1, y: 0.45, w: 0.5, h: 0.1 }, scale: 1,
    })
    const viewId2 = /视图 ([0-9a-f-]{36})/.exec(view2.result ?? '')?.[1]
    const bad1 = await by.get('desktop_click_candidate').execute({
      hwnd: win.hwnd, shotId: shotId2, viewId: viewId2, point: { u: 1.2, v: 0.5 }, observe: false,
    }).catch((e) => ({ err: e.message }))
    if (/0~1 之间|OutOfRange/.test(bad1.err ?? '')) ok('u>1 被拒绝（不夹边）')
    else bad(`越界报点没拒绝：${JSON.stringify(bad1).slice(0, 140)}`)

    const bad2 = await by.get('desktop_click_candidate').execute({
      hwnd: win.hwnd, shotId: shotId2, viewId: 'no-such-view', point: { u: 0.5, v: 0.5 }, observe: false,
    }).catch((e) => ({ err: e.message }))
    if (/找不到视图/.test(bad2.err ?? '')) ok('不存在的 viewId 被拒绝')
    else bad(`不存在的 viewId 没拒绝：${JSON.stringify(bad2).slice(0, 140)}`)

    const bad3 = await by.get('desktop_click_candidate').execute({
      hwnd: win.hwnd, shotId: shotId2, observe: false,
    }).catch((e) => ({ err: e.message }))
    if (/要么给 candidateId/.test(bad3.err ?? '')) ok('两种目标都不给 → 明确报错')
    else bad(`缺参数没报错：${JSON.stringify(bad3).slice(0, 140)}`)
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
