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

// ── 顺带检查：不能有硬编码的本机工作区目录名 ──
//
// 出过一次真事故：判断"闸门位置对不对"用的是正则匹配路径里有没有某个
// 固定字样，而那等于写死了一台机器的目录名。作者本机永远匹配得上，
// 别人的工作区永远匹配不上 —— 于是正常情况被报成
// "★ 闸门位置不正确 —— ESC 会只关界面而不拦住操作"，
// 让人以为急停坏了，而实际是好的。
//
// 这类问题在自己的机器上**永远不会暴露**，所以必须靠断言拦住。
const hardcodedLines = []
// index.mjs 也要查：判定"闸门位置对不对"的逻辑在那里，而正是那一处出过事故。
// 只查 toolset.mjs 会漏掉——第一版就是这么漏的（注入到 index.mjs 却没被报出来）。
const siblings = [file, file.replace(/toolset\.mjs$/, 'index.mjs')]
for (const f of siblings) {
  let text
  try {
    text = readFileSync(f, 'utf8')
  } catch {
    continue
  }
  const short = f.split(/[\\/]/).pop()
  for (const m of text.matchAll(/^.*\bdsh workspace\b.*$/gm)) {
    // 注释里提到它没关系（那是说明历史），代码里用就不行
    const line = m[0].trim()
    if (!/^(\/\/|\*|\/\*)/.test(line)) {
      hardcodedLines.push(`${short}: ${line.slice(0, 90)}`)
    }
  }
}
const hasHardcodedWorkspace = hardcodedLines.length > 0

// ── 工具描述不能与行为矛盾 ──
//
// 工具描述是**模型读到的契约**。它过时了，模型就会照着对用户说假话，
// 而这件事没有任何报错：代码对不对是另一回事，描述错了照样"通过"。
//
// 实测踩到过：desktop_off 的描述写着"这是本插件唯一的停止方式——
// 本环境无法捕获全局按键，因此没有'按 ESC 停'"，而那时 ESC 早就
// 实机验证过两次能用。用户问"怎么停"，模型会照描述答"没有 ESC"。
//
// 这类问题只能靠已知的过时说法清单来拦——不优雅，但比没有强。
const STALE_CLAIMS = [
  '无法捕获全局按键', // ESC 钩子早已可用
  '唯一的停止', // ESC / 逃生条 / desktop_off 三条路
  '不受限制', // 读树与截图现在要授权
  '不受白名单限制', // 同上；措辞也不准确（用的是会话授权）
]
const staleFound = []
for (const f of siblings) {
  let text
  try {
    text = readFileSync(f, 'utf8')
  } catch {
    continue
  }
  const short = f.split(/[\\/]/).pop()
  for (const claim of STALE_CLAIMS) {
    for (const m of text.matchAll(new RegExp(`^.*${claim}.*$`, 'gm'))) {
      const line = m[0].trim()
      if (!/^(\/\/|\*|\/\*)/.test(line)) staleFound.push(`${short}: ${line.slice(0, 90)}`)
    }
  }
}

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

if (problems.length === 0 && !hasHardcodedWorkspace && staleFound.length === 0) {
  console.log('  ✓ 授权策略符合预期：')
  for (const s of summary) console.log(s)
  console.log('  ✓ 无硬编码的本机工作区路径')
  console.log('  ✓ 工具描述无过时说法')
  process.exit(0)
}

if (hasHardcodedWorkspace) {
  console.log('  ✗ 发现硬编码的本机工作区目录名：')
  for (const line of hardcodedLines) console.log(`      ${line}`)
  console.log('      插件的判定不能依赖某台机器的目录名——换一个工作区就会误报。')
  console.log('      需要判断位置时用 workspaceRegistry 反查，或改为功能性探测。')
}

if (staleFound.length > 0) {
  console.log('  ✗ 工具描述里有与行为矛盾的说法：')
  for (const line of staleFound) console.log(`      ${line}`)
  console.log('      描述是模型读到的契约；它过时了，模型就会照着对用户说假话，')
  console.log('      而这件事不会有任何报错。改描述，或把这条说法从清单里去掉（仅当它已不适用）。')
}

if (problems.length > 0) {
  console.log('  ✗ 授权策略不符：')
  for (const p of problems) console.log(`      ${p}`)
  console.log('      读树与截图**必须**走授权——它们会读到界面全部内容。')
}

process.exit(1)
