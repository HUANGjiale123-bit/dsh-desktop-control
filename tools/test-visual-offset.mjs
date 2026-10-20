#!/usr/bin/env node
/* 回归：裁剪 OCR 的坐标偏移必须同时作用在**行框和词框**上。
 *
 * 背景（复核 R4）：裁图原点是 (1000,200)，OCR 在裁图里识别到的词框是
 * (100,100,60,20)，正确点击中心是 (1130,310)。旧实现只平移 row.quad，
 * 没平移 row.words；带 query 时候选优先用命中词的框，于是把局部坐标
 * 当成原图坐标交给输入层，实际点了 (130,110) —— 整整偏了裁图原点。
 *
 * 这个测试走的是**候选生成的真实函数**，断言最终 pointInSourceImage。
 *
 * 用法：node tools/test-visual-offset.mjs
 */

import { shiftOcrRows, candidatesFromOcr } from '../server/visual-candidates.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const eq = (a, b) => Math.abs(Number(a) - Number(b)) < 0.51

// 一份"裁图内"的 OCR 行：整行框 + 两个词，query 命中第二个词
const row = {
  label: '保存 文件',
  quad: [
    [100, 100], [260, 100], [260, 120], [100, 120],
  ],
  score: 0.93,
  words: [
    { text: '保存', x: 100, y: 100, w: 60, h: 20 },
    { text: '文件', x: 170, y: 100, w: 70, h: 20 },
  ],
}

const DX = 1000
const DY = 200

console.log()
console.log('── 1. 行框与词框一起平移 ──')
const shifted = shiftOcrRows([row], DX, DY)
const s0 = shifted[0]
if (eq(s0.quad[0][0], 1100) && eq(s0.quad[0][1], 300)) ok('行框平移正确')
else bad(`行框没平移对：${JSON.stringify(s0.quad[0])}`)
if (eq(s0.words[0].x, 1100) && eq(s0.words[0].y, 300)) ok('词框平移正确')
else bad(`词框没平移对：${JSON.stringify(s0.words[0])}`)

console.log()
console.log('── 2. 带 query 的候选中心 = 命中词中心（不是整行中心）──')
const cands = candidatesFromOcr(shifted, {
  shotId: 's', viewId: 'v', ownerId: 'o', query: '文件',
})
const c = cands[0]
// 命中词"文件"在裁图内是 (170..240, 100..120)，平移后 (1170..1240, 300..320)
const wantX = 1170 + 70 / 2
const wantY = 300 + 20 / 2
if (c && eq(c.pointInSourceImage.x, wantX) && eq(c.pointInSourceImage.y, wantY)) {
  ok(`候选中心 (${c.pointInSourceImage.x}, ${c.pointInSourceImage.y}) = 命中词中心`)
} else {
  bad(`候选中心错了：期望 (${wantX},${wantY})，得到 ${JSON.stringify(c?.pointInSourceImage)}`)
}

console.log()
console.log('── 3. 复核给的具体场景：(1130,310) ──')
// 复核的示例：词框 (100,100,60,20) 在裁图内，点击中心应为 (1130,310)
const one = shiftOcrRows([{
  label: '时间/日期',
  quad: [[100, 100], [160, 100], [160, 120], [100, 120]],
  words: [{ text: '时间/日期', x: 100, y: 100, w: 60, h: 20 }],
}], 1000, 200)
const c2 = candidatesFromOcr(one, { shotId: 's', viewId: 'v', ownerId: 'o', query: '时间' })[0]
if (c2 && eq(c2.pointInSourceImage.x, 1130) && eq(c2.pointInSourceImage.y, 310)) {
  ok('(1130,310) 正确')
} else {
  bad(`期望 (1130,310)，得到 ${JSON.stringify(c2?.pointInSourceImage)}`)
}

console.log()
console.log('── 4. 无 query 时用整行框，也要在原图坐标 ──')
const c3 = candidatesFromOcr(shifted, { shotId: 's', viewId: 'v', ownerId: 'o' })[0]
if (c3 && eq(c3.boxInSourceImage.x, 1100) && eq(c3.boxInSourceImage.y, 300)) {
  ok('无 query 时整行框已平移')
} else {
  bad(`无 query 时框错了：${JSON.stringify(c3?.boxInSourceImage)}`)
}

console.log()
console.log('── 5. 零偏移路径不受影响 ──')
const same = shiftOcrRows([row], 0, 0)
if (same[0] === row) ok('零偏移直接返回原数组（不复制）')
else bad('零偏移应当原样返回')

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
