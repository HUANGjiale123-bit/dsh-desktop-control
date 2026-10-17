// 截图文件必须有保留上限。
//
// ── 这个问题是用户问出来的 ──
//
// "你每次截图用完后会删掉吗？不删掉的话会占内存吧？"
//
// 查了一下：从不删除。67 个文件 32 MB，最老的躺了两天。一天几十次操作
// 就是几十 MB/天，而这是一个每天都用的插件。
//
// 现在保留最近 SHOT_KEEP 张，同时删掉超过 SHOT_MAX_AGE_MS 的。
// 刚拍的那张永远不删（调用方马上要读它）。
//
// ── 为什么用 includes 而不是正则 ──
//
// 写这个测试时连续四次误报，全是正则被多层转义吃掉。静态检查用子串判断
// 够了，正则只在需要描述结构时才用 —— 而这里每个检查点都是一段**固定文本**。
//
// 用法：node tools/test-shot-prune.mjs

import { readFileSync, readdirSync } from 'node:fs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const src = readFileSync('server/toolset.mjs', 'utf8')

console.log()
console.log('── 1. 保留策略存在 ──')

const checks = [
  ['const SHOT_KEEP = ', '有数量上限'],
  ['const SHOT_MAX_AGE_MS = ', '有时间上限'],
  ['async function pruneShots', '有 pruneShots 实现'],
  ['await pruneShots(path)', '截图之后会调用清理'],
  ['await unlink(f)', '确实会删文件'],
]
for (const [needle, label] of checks) {
  if (src.includes(needle)) ok(label)
  else bad(`${label}（找不到 "${needle}"）`)
}

console.log()
console.log('── 2. 清理是尽力而为，不能连累截图 ──')

const i = src.indexOf('async function pruneShots')
const body = src.slice(i, i + 2000)
for (const [needle, label] of [
  ['try {', '有 try'],
  ['catch {', '有 catch'],
  ['return 0', '失败时返回 0 而不是抛出'],
]) {
  if (body.includes(needle)) ok(label)
  else bad(`pruneShots ${label}（找不到 "${needle}"）`)
}

console.log()
console.log('── 3. 刚拍的那张不能被删掉 ──')

if (body.includes('currentPath')) ok('pruneShots 接收当前文件路径')
else bad('pruneShots 不知道哪个是刚拍的')

if (body.includes('if (s.path === currentPath) return')) ok('当前文件被排除在删除之外')
else bad('可能把刚拍的删掉 —— 调用方读到的是不存在的文件')

console.log()
console.log('── 4. 数值合理 ──')

const keepLine = src.split('\n').find((l) => l.includes('const SHOT_KEEP = '))
const n = Number((keepLine ?? '').replace(/[^0-9]/g, ''))
if (n > 0 && n <= 200) ok(`保留 ${n} 张`)
else bad(`SHOT_KEEP 数值不合理：${keepLine ?? '读不到'}`)

const ageLine = src.split('\n').find((l) => l.includes('const SHOT_MAX_AGE_MS = '))
if (ageLine && ageLine.includes('60 * 60')) ok(`时间上限：${ageLine.trim().replace('const SHOT_MAX_AGE_MS = ', '')}`)
else bad(`时间上限看起来不像小时量级：${ageLine ?? '读不到'}`)

console.log()
console.log('── 5. 清理必须挂在截图上，而不是靠谁记得手动跑 ──')

// 找到 screenshot 方法体，确认里面有 prune（而不是挂在别处）
const si = src.indexOf('async screenshot({ hwnd }) {')
if (si < 0) {
  bad('找不到 screenshot 方法')
} else {
  const shotBody = src.slice(si, si + 3000)
  if (shotBody.includes('await pruneShots(path)')) ok('每次截图都会顺手清理')
  else bad('截图没有清理 —— 又要靠人记得')
}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  ${pass} 项通过` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
