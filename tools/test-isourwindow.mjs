// 验证测试里的 isOurs 过滤器真的能识别 Windows 路径。
//
// 这一条单独验，因为它的第一版写错了却"看起来能用"：
//   split(/[\/]/)   —— 只认 `/`，不认 `\`
//   而 Windows 的 exe 路径全是反斜杠，于是 split 切不开，pop 拿到整条路径，
//   正则永远不匹配 → 过滤器形同虚设。
// 而它失效的表现不是报错，是"测试莫名其妙选中了不该选的窗口"。

import { readFileSync } from 'node:fs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

// 从测试文件里把真实的定义取出来，避免这里另写一份（那样测的就不是同一个东西）
const src = readFileSync(new URL('./test-helper-cli.mjs', import.meta.url), 'utf8')
const m = src.match(/const isOurs = [\s\S]*?\n/)
if (!m) {
  console.log('  ✗ 找不到 isOurs 定义')
  process.exit(1)
}
const isOurs = eval(`(${m[0].replace(/^const isOurs = /, '').trim().replace(/;$/, '')})`)

console.log()
console.log('── isOurs 必须同时认 Windows 反斜杠和正斜杠 ──')

const cases = [
  ['C:\\Program Files\\dsh\\dsc-esc.exe', true, '反斜杠路径 + 光带程序'],
  ['C:/Program Files/dsh/dsc-esc.exe', true, '正斜杠路径 + 光带程序'],
  ['C:\\Program Files\\dsh\\dsc.exe', true, '反斜杠路径 + 主程序'],
  ['C:\\Windows\\System32\\notepad.exe', false, '普通应用'],
  ['D:\\QQMusic\\QQMusic.exe', false, '普通应用'],
  ['', false, '空字段（窗口可能没有 exe）'],
]

for (const [exe, want, label] of cases) {
  const got = isOurs({ exe })
  if (got === want) ok(`${label}：${exe || '(空)'} → ${got}`)
  else bad(`${label}：${exe} → ${got}，期望 ${want}`)
}

console.log()
console.log('── 字段缺失时不能崩 ──')

for (const bad_ of [{}, { exe: undefined }, { exe: null }]) {
  try {
    isOurs(bad_)
    ok(`容忍 ${JSON.stringify(bad_)}`)
  } catch (e) {
    bad(`${JSON.stringify(bad_)} 抛异常：${e.message}`)
  }
}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
