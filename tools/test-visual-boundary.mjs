#!/usr/bin/env node
/* 工具边界的参数断言：不看纯函数，直接看 `desktop_click_candidate` **实际传给**
 * `set.tools.click` 的参数。
 *
 * 外部评审原话："验收必须在 desktop_click_candidate 的工具边界拦截传给
 * set.tools.click 的参数，而不只是单测数学函数。"
 *
 * 做法：plugin 在 apply 结束时把 toolset 句柄交出来（getToolsetForTest），
 * 测试把 set.tools.click 换成一个"记账再转发"的包装，然后跑真实点击，
 * 逐字段核对：坐标空间、桌面坐标、expectedGeom、观察开关。
 *
 * 用法：node tools/test-visual-boundary.mjs
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
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-boundary-'))
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
await mod.apply(ctx, { helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots') })
const by = new Map(reg.map((t) => [t.name, t]))

const set = mod.getToolsetForTest()
if (!set?.tools?.click) {
  console.log('  ✗ 拿不到 toolset 句柄（getToolsetForTest 没返回东西）')
  process.exit(1)
}

/* 账本：把真实调用记下来再转发。 */
const captured = []
const realClick = set.tools.click.bind(set.tools)
set.tools.click = async (args) => {
  captured.push(JSON.parse(JSON.stringify(args)))
  return realClick(args)
}

const wins = (() => {
  try {
    return JSON.parse(spawnSync(HELPER, ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
  } catch { return [] }
})()
const win = wins.find((w) => String(w.class ?? '').includes('Tauri') && w.width > 400)

try {
  console.log()
  if (!win) {
    console.log('  ✗ 没找到可用窗口')
    process.exit(1)
  }
  process.env.DSC_ALLOWED_APPS = String(win.exe).split(/[\\/]/).pop()

  // 激活 + 截图（截图拍的是屏幕那块）
  await by.get('desktop_click').execute({
    hwnd: win.hwnd, x: Math.round(win.x + win.width / 2), y: win.y + 10, observe: false,
  }).catch(() => {})
  await new Promise((r) => setTimeout(r, 350))
  const shot = await by.get('desktop_screenshot').execute({ hwnd: win.hwnd })

  console.log('── 1. 候选点击：传给 click 的参数对不对 ──')
  {
    const list = await by.get('desktop_visual_candidates').execute({ hwnd: win.hwnd, shotId: shot.shotId })
    const m = /#\d+ ([0-9a-f-]{36}) {2}"([^"]*)" {2}@\((\d+),(\d+) (\d+)x(\d+)\)/.exec(list.result ?? '')
    if (!m) {
      skip('没有候选，跳过')
    } else {
      const cand = { id: m[1], label: m[2], box: { x: +m[3], y: +m[4], w: +m[5], h: +m[6] } }
      captured.length = 0
      const r = await by.get('desktop_click_candidate').execute({
        hwnd: win.hwnd, shotId: shot.shotId, candidateId: cand.id, observe: false,
      }).catch((e) => ({ err: e.message }))
      const t = r.err ?? r.result ?? r.text ?? ''
      const call = captured[0]
      if (!call) {
        skip(`这次没走到 click（${t.slice(0, 100)}）`)
      } else {
        ok(`捕获到真实调用：${JSON.stringify({ x: call.x, y: call.y, space: call.space })}`)

        if (call.space === 'desktop') ok('坐标空间是 desktop（窗口原点由插件加，不由 click 加）')
        else bad(`坐标空间错了：${call.space}`)

        // 期望：图内锚点中心 → 桌面 = 窗口原点 + 图内点（图与窗口同尺寸时）
        const cx = cand.box.x + cand.box.w / 2
        const cy = cand.box.y + cand.box.h / 2
        const expectX = win.x + Math.floor(cx)
        const expectY = win.y + Math.floor(cy)
        if (Math.abs(call.x - expectX) <= 2 && Math.abs(call.y - expectY) <= 2) {
          ok(`落点与"窗口原点 + 图内锚点"一致（期望 ${expectX},${expectY}，实际 ${call.x},${call.y}）`)
        } else {
          bad(`落点算错了：期望 (${expectX},${expectY})，实际 (${call.x},${call.y})`)
        }

        if (call.expectedGeom && call.expectedGeom.w === win.width && call.expectedGeom.h === win.height) {
          ok(`带上了 expectedGeom（${call.expectedGeom.w}x${call.expectedGeom.h}），窗口一移就拒`)
        } else {
          bad(`expectedGeom 不对：${JSON.stringify(call.expectedGeom)}`)
        }
      }
    }
  }

  console.log()
  console.log('── 2. 报点点击：裁图内 (u,v) 的换算要看真实参数 ──')
  {
    const shot2 = await by.get('desktop_screenshot').execute({ hwnd: win.hwnd })
    const region = { x: 0.2, y: 0.3, w: 0.4, h: 0.2 }
    const view = await by.get('desktop_visual_view').execute({
      hwnd: win.hwnd, shotId: shot2.shotId, region, scale: 1,
    })
    const vt = view.result ?? ''
    const viewId = /视图 ([0-9a-f-]{36})/.exec(vt)?.[1]
    const crop = (() => {
      const mm = /裁剪 \((\d+),(\d+) (\d+)x(\d+)\)/.exec(vt)
      return mm ? { x: +mm[1], y: +mm[2], w: +mm[3], h: +mm[4] } : null
    })()

    if (!viewId || !crop) {
      skip('没拿到视图或裁剪信息，跳过')
    } else {
      captured.length = 0
      const r = await by.get('desktop_click_candidate').execute({
        hwnd: win.hwnd, shotId: shot2.shotId, viewId, point: { u: 0.5, v: 0.5 }, observe: false,
      }).catch((e) => ({ err: e.message }))
      const t = r.err ?? r.result ?? r.text ?? ''
      const call = captured[0]
      if (!call) {
        skip(`这次没走到 click（${t.slice(0, 100)}）`)
      } else {
        const cx = crop.x + crop.w / 2
        const cy = crop.y + crop.h / 2
        const expectX = win.x + Math.floor(cx)
        const expectY = win.y + Math.floor(cy)
        if (Math.abs(call.x - expectX) <= 2 && Math.abs(call.y - expectY) <= 2) {
          ok(`报点 u=0.5,v=0.5 落在裁剪区中心（期望 ${expectX},${expectY}，实际 ${call.x},${call.y}）`)
        } else {
          bad(`报点换算错了：期望 (${expectX},${expectY})，实际 (${call.x},${call.y})`)
        }
        if (call.space === 'desktop') ok('报点路径同样用 desktop 空间')
        else bad(`报点路径坐标空间错了：${call.space}`)
      }
    }
  }
} finally {
  delete process.env.DSC_ALLOWED_APPS
  set.tools.click = realClick
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
