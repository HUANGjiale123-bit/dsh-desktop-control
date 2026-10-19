#!/usr/bin/env node
/* 端到端验证 space:"normalized" —— 真的走一次 toolset。
 *
 * ── 为什么从报错里取坐标 ──
 *
 * 在沙箱里 SetCursorPos 会被拒绝（Win32 错误 5: 拒绝访问）。那不是换算错误 ——
 * 换算发生在调用 helper 之前，而报错信息里**带着换算结果**：
 *
 *     Refused — SetCursorPos(1121,669) 失败 (Win32 错误 5: 拒绝访问。)
 *
 * 所以从这行里抽坐标来验证换算，比要求光标真的移动更可靠，
 * 而且在没有输入权限的环境里也能跑。
 *
 * ── 为什么用 move 不用 click ──
 *
 * move 只移动光标不按下。验证阶段没有理由真的点下去。
 */

import { createRequire } from 'node:module'
import { pathToFileURL } from 'node:url'

const require = createRequire(import.meta.url)
const path = require('node:path')

const results = []
let pass = 0
let fail = 0
const ok = (m) => { results.push(`  ✓ ${m}`); pass++ }
const bad = (m) => { results.push(`  ✗ ${m}`); fail++ }

const toolsetPath = pathToFileURL(path.resolve('server/toolset.mjs')).href
const { createDesktopToolset } = await import(toolsetPath)

const set = createDesktopToolset({})

const listRes = await set.tools.listWindows({})
const windows = listRes.windows ?? []
const target = windows.find((w) => w.width > 300 && w.height > 300)
if (!target) {
  console.log('  ✗ 没有找到可用的窗口来验证')
  process.exit(1)
}
results.push(`  目标窗口: hwnd=${target.hwnd} ${target.width}x${target.height}，原点 (${target.x}, ${target.y})`)
results.push('')

/** 从 helper 的报错里抽出它实际请求的光标位置 */
function askedPoint(text) {
  const m = text.match(/SetCursorPos\((-?\d+),\s*(-?\d+)\)/)
  if (!m) return null
  return { x: Number(m[1]), y: Number(m[2]) }
}

results.push('── 1. normalized (0.5, 0.5) 应换算到窗口中心 ──')
{
  const r = await set.tools.move({ hwnd: target.hwnd, x: 0.5, y: 0.5, space: 'normalized' })
  const text = r.text ?? ''
  const expX = target.x + Math.round(0.5 * target.width)
  const expY = target.y + Math.round(0.5 * target.height)
  const got = askedPoint(text)

  if (!got) {
    bad(`返回里没有坐标，无法验证。原文：${text.slice(0, 200)}`)
  } else if (got.x === expX && got.y === expY) {
    ok(`换算成 (${got.x}, ${got.y}) = 原点 + 半宽高，等于窗口中心`)
  } else {
    bad(`换算错了：得到 (${got.x}, ${got.y})，应为 (${expX}, ${expY})`)
  }

  if (r.isError) {
    results.push(`  · 光标未实际移动（沙箱拦输入，已知）：${text.split('\n')[0].slice(0, 70)}`)
  } else {
    ok('光标实际移动成功')
  }
}

results.push('')
results.push('── 2. 另一个比例，验证不是碰巧 ──')
{
  const r = await set.tools.move({ hwnd: target.hwnd, x: 0.25, y: 0.75, space: 'normalized' })
  const text = r.text ?? ''
  const expX = target.x + Math.round(0.25 * target.width)
  const expY = target.y + Math.round(0.75 * target.height)
  const got = askedPoint(text)
  if (got && got.x === expX && got.y === expY) {
    ok(`(0.25, 0.75) → (${got.x}, ${got.y})，两轴独立换算`)
  } else {
    bad(`(0.25, 0.75) 换算错：${got ? `(${got.x}, ${got.y})` : '没拿到'}，应为 (${expX}, ${expY})`)
  }
}

results.push('')
results.push('── 3. 越界必须被拒绝，且不碰 helper ──')
{
  const cases = [
    [{ x: 1.5, y: 0.5 }, 'x=1.5'],
    [{ x: 0.5, y: -0.1 }, 'y=-0.1'],
  ]
  let allGood = true
  for (const [pt, label] of cases) {
    const r = await set.tools.move({ hwnd: target.hwnd, x: pt.x, y: pt.y, space: 'normalized' })
    const t = r.text ?? ''
    if (!r.isError) { bad(`${label} 没有被拒绝`); allGood = false; continue }
    if (!t.includes('0..1')) { bad(`${label} 被拒但没说取值范围`); allGood = false; continue }
    if (askedPoint(t)) { bad(`${label} 被拒之前还是调了 helper`); allGood = false }
  }
  if (allGood) ok('两种越界都被拒绝、说明范围、且没发到 helper')
}

results.push('')
results.push('── 4. desktop 空间语义不变（纯平移）──')
{
  const wantX = target.x + 10
  const wantY = target.y + 10
  const r = await set.tools.move({ hwnd: target.hwnd, x: wantX, y: wantY })
  const t = r.text ?? ''
  const got = askedPoint(t)
  if (got && got.x === wantX && got.y === wantY) {
    ok(`desktop (${wantX}, ${wantY}) 原样传给 helper，没有额外换算`)
  } else if (!got) {
    bad(`desktop 空间行为不明：${t.slice(0, 160)}`)
  } else {
    bad(`desktop 空间被多算了：得到 (${got.x}, ${got.y})，应为 (${wantX}, ${wantY})`)
  }
}

results.push('')
results.push('── 5. window 空间语义不变（纯平移 + 原点）──')
{
  const ix = 100
  const iy = 200
  const r = await set.tools.move({ hwnd: target.hwnd, x: ix, y: iy, space: 'window' })
  const text = r.text ?? ''
  const got = askedPoint(text)
  const expX = target.x + ix
  const expY = target.y + iy
  if (got && got.x === expX && got.y === expY) {
    ok(`window (${ix}, ${iy}) → (${got.x}, ${got.y})，等于原点 + 图内坐标`)
  } else {
    bad(`window 空间换算错：${got ? `(${got.x}, ${got.y})` : '没拿到'}，应为 (${expX}, ${expY})`)
  }
}

console.log(results.join('\n'))
console.log('')
console.log('══════════════════════════════')
if (fail === 0) console.log(`  ${pass} 项通过`)
else console.log(`  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
