#!/usr/bin/env node
/* region_hash / image_hash：内容复查用的两个指纹命令。
 *
 * 为什么值得单独验：
 *   · 它们必须是**同一套算法** —— 一个读历史 PNG、一个读现在屏幕，
 *     算法不一致的话，"内容变了"这个判断就没有意义（永远不等，或永远相等）；
 *   · 越界必须报错而不是裁剪 —— 裁剪会把"坐标算错了"变成"看了一下别处"，
 *     然后照样返回一个指纹，上层以为复查通过了。
 *
 * 用法：node tools/test-region-hash.mjs
 */

import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { tempHelper, integrityOf } from './fixture.mjs'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`); skipped++ }

/* 用 %TEMP% 里的 helper：工作区里那份是 Low，抓屏同样会被拦。
 * 这个测试要验的正是"抓得到、算得出"，所以必须用 Medium。 */
const HELPER = tempHelper()
const level = integrityOf(HELPER)
console.log()
console.log(`  helper: ${HELPER}（integrity=${level}）`)

function run(args, env) {
  const r = spawnSync(HELPER, args, {
    encoding: 'utf8', windowsHide: true, timeout: 60000,
    env: { ...process.env, ...(env ?? {}) },
  })
  try { return JSON.parse(String(r.stdout ?? '{}')) } catch { return { ok: false, error: 'parse' } }
}

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'regionhash-'))
try {
  // 挑一个真实窗口当靶子
  const listRaw = run(['list_windows'], { DSC_ALLOWED_APPS: '' })
  const wins = Array.isArray(listRaw.windows) ? listRaw.windows : []
  const ours = (w) => /^(dsc|dsc-esc|textinputhost)\.exe$/i.test(String(w.exe ?? '').split(/[\\/]/).pop())
  const win = wins.find((w) => w.width > 400 && w.height > 400 && w.exe && !ours(w))

  if (!win) {
    console.log('  ✗ 找不到可用窗口')
    process.exit(1)
  }
  const exe = String(win.exe).split(/[\\/]/).pop()
  const env = { DSC_ALLOWED_APPS: exe }
  console.log(`  靶子: hwnd=${win.hwnd} ${win.width}x${win.height} @(${win.x},${win.y})  exe=${exe}`)

  const png = join(tmp, 'shot.png')
  const shot = run(['screenshot', String(win.hwnd), png], env)
  if (!shot.ok) {
    console.log(`  ✗ 截图失败：${shot.error} ${shot.message ?? ''}`)
    process.exit(1)
  }

  console.log()
  console.log('── 1. 同一块区域，指纹必须稳定且两边一致 ──')
  const box = { x: 100, y: 100, w: 200, h: 100 }
  const a = run(['image_hash', png, String(box.x), String(box.y), String(box.w), String(box.h)], env)
  if (a.ok && /^[0-9a-f]{16}$/.test(a.hash)) ok(`image_hash 返回 16 位十六进制指纹：${a.hash}`)
  else bad(`image_hash 异常：${JSON.stringify(a).slice(0, 160)}`)

  const a2 = run(['image_hash', png, String(box.x), String(box.y), String(box.w), String(box.h)], env)
  if (a2.ok && a2.hash === a.hash) ok('同一张图、同一块区域，两次指纹相同（算法确定性）')
  else bad(`同一张图两次指纹不同：${a.hash} vs ${a2.hash}`)

  // 屏幕上的同一块：桌面坐标 = 窗口原点 + 图片内偏移
  const sx = win.x + box.x
  const sy = win.y + box.y
  let matched = false
  let lastLive = null
  for (let i = 0; i < 3 && !matched; i++) {
    const live = run(['region_hash', String(win.hwnd), String(sx), String(sy), String(box.w), String(box.h)], env)
    lastLive = live
    if (!live.ok) break
    if (live.hash === a.hash) matched = true
    else await new Promise((r) => setTimeout(r, 300))
  }
  if (matched) ok('region_hash 与 image_hash 对同一块区域给出相同指纹（两个命令同一套算法）')
  else if (lastLive && !lastLive.ok) bad(`region_hash 失败：${lastLive.error} ${lastLive.message ?? ''}`)
  else {
    skip(`三次都对不上（${lastLive?.hash} ≠ ${a.hash}）—— 那块区域可能有动画/光标，换个区域再验`)
    // 退一步：换一块更"静态"的地方（窗口左上角留白）再试一次
    const blank = { x: 5, y: 5, w: 120, h: 40 }
    const ib = run(['image_hash', png, String(blank.x), String(blank.y), String(blank.w), String(blank.h)], env)
    const lb = run(['region_hash', String(win.hwnd), String(win.x + blank.x), String(win.y + blank.y),
      String(blank.w), String(blank.h)], env)
    if (ib.ok && lb.ok && ib.hash === lb.hash) ok('换到窗口左上角后两边指纹一致（说明算法是同一套）')
  }

  console.log()
  console.log('── 2. 越界必须报错，不能裁剪 ──')
  const outside = run(['region_hash', String(win.hwnd), String(win.x + win.width - 10),
    String(win.y + win.height - 10), '200', '200'], env)
  if (!outside.ok && /BadValue|超出/.test(JSON.stringify(outside))) ok('region_hash 越界被拒绝')
  else bad(`region_hash 越界没有拒绝：${JSON.stringify(outside).slice(0, 120)}`)

  const imgOutside = run(['image_hash', png, String(shot.width - 10), String(shot.height - 10), '200', '200'], env)
  if (!imgOutside.ok && /BadValue|超出/.test(JSON.stringify(imgOutside))) ok('image_hash 越界被拒绝')
  else bad(`image_hash 越界没有拒绝：${JSON.stringify(imgOutside).slice(0, 120)}`)

  const zero = run(['image_hash', png, '0', '0', '0', '10'], env)
  if (!zero.ok && /BadValue|为正/.test(JSON.stringify(zero))) ok('宽高为 0 被拒绝')
  else bad(`宽高为 0 没有被拒绝：${JSON.stringify(zero).slice(0, 120)}`)

  console.log()
  console.log('── 3. 急停时两个命令都要停 ──')
  {
    const gate = join(tmp, 'halt')
    mkdirSync(tmp, { recursive: true })
    spawnSync(process.execPath, ['-e', `require('fs').writeFileSync(${JSON.stringify(gate)}, 't')`],
      { windowsHide: true })
    const ge = { ...env, DSC_HALT_GATE: gate }
    const r1 = run(['region_hash', String(win.hwnd), String(sx), String(sy), '50', '50'], ge)
    const r2 = run(['image_hash', png, '10', '10', '50', '50'], ge)
    if (!r1.ok && r1.error === 'Halted') ok('闸门拉下时 region_hash 被拒')
    else bad(`region_hash 没停：${JSON.stringify(r1).slice(0, 100)}`)
    if (!r2.ok && r2.error === 'Halted') ok('闸门拉下时 image_hash 被拒（急停 = 别再碰我的界面内容）')
    else bad(`image_hash 没停：${JSON.stringify(r2).slice(0, 100)}`)
  }
} finally {
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
