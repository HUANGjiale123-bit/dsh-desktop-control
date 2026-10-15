// 检查观察类工具的授权策略。
//
// 为什么需要它：读界面内容（无障碍树）和截图**原先不需要授权**，
// 于是助手可以在不问用户的情况下读到观影记录、聊天窗口。
// 这个回退不会报错、不会崩，只会静默泄露，所以必须靠断言守住。
//
// 期望的边界：
//   需要授权：readWindow（读到界面全部文字）、screenshot（截图就是界面内容）
//   不需授权：listWindows（只给句柄/进程/尺寸，已刻意去掉标题）
//             listApps（只给应用名与路径）
//
// 用法：node tools/check-authpolicy.mjs <toolset.mjs 路径>
//
// 退出码：0 = 符合，1 = 不符合，2 = 用法/读取错误。
// **参数缺失或读不到文件时必须报错退出**，不能默默通过——
// 这一点是踩过坑的：早先的版本把参数传丢、又把错误吞了，
// 结果故意破坏代码它照样报 OK，等于没有检查。

import { readFileSync } from 'node:fs'

const file = process.argv[2]
if (!file) {
  console.error('用法：node tools/check-authpolicy.mjs <toolset.mjs 路径>')
  process.exit(2)
}

let src
try {
  src = readFileSync(file, 'utf8')
} catch (e) {
  console.error(`读不到 ${file}: ${e.message}`)
  process.exit(2)
}

// 期望：true = 该函数体内必须出现授权调用
const WANT = {
  readWindow: true,
  screenshot: true,
  listWindows: false,
  listApps: false,
}

/** 切出某个方法的函数体：从 "async name(" 到下一个同级 "async "。 */
function bodyOf(src, name) {
  const i = src.indexOf(`async ${name}(`)
  if (i < 0) return null
  const nl = String.fromCharCode(10)
  const j = src.indexOf(nl + '    async ', i + 10)
  return src.slice(i, j < 0 ? src.length : j)
}

const problems = []
const summary = []

for (const [name, needAuth] of Object.entries(WANT)) {
  const body = bodyOf(src, name)
  if (body === null) {
    problems.push(`${name}: 找不到这个方法（方法被改名或删除了？）`)
    continue
  }
  const hasAuth = body.includes('await authorize(') || body.includes('await authorizeApp(')
  summary.push(`      ${name.padEnd(12)} ${hasAuth ? '需授权' : '不需授权'}`)
  if (hasAuth !== needAuth) {
    problems.push(
      `${name}: 期望${needAuth ? '需授权' : '不需授权'}，实际${hasAuth ? '需授权' : '不需授权'}`,
    )
  }
}

if (problems.length === 0) {
  console.log('  ✓ 授权策略符合预期：')
  for (const s of summary) console.log(s)
  process.exit(0)
}

console.log('  ✗ 授权策略不符：')
for (const p of problems) console.log(`      ${p}`)
console.log('      读树与截图**必须**走授权——它们会读到界面全部内容。')
process.exit(1)
