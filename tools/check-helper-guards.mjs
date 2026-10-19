// 检查 helper 的每个命令是否都受【急停闸门】和【白名单】约束。
//
// ── 为什么需要它 ──
//
// 这两道检查是逐命令手写的，所以每次新增命令或改动既有命令都可能漏一个，
// 而漏掉不会有任何报错——命令只是"照常工作"。实测踩到两次：
//
//   · tree / screenshot 漏了白名单 → 空白名单下照样读完整界面文字
//   · tree / screenshot / launch 漏了急停 → 闸门拉下后照样读、照样启动程序
//
// 第二条尤其要紧：用户按 ESC 的意图是"别再碰我的电脑"。如果界面消失了、
// 点击被拒了，而读取和启动还在跑，那道闸门就是假的。
//
// 所以这里逐个命令断言这两道检查都在。哪些命令需要它们由下面的表说明，
// 新加命令时如果忘了归类，脚本会报"未分类"而不是默默放过。
//
// 用法：node tools/check-helper-guards.mjs <Program.cs 路径>

import { readFileSync } from 'node:fs'

const file = process.argv[2]
if (!file) {
  console.error('用法：node tools/check-helper-guards.mjs <Program.cs 路径>')
  process.exit(2)
}

let src
try {
  src = readFileSync(file, 'utf8')
} catch (e) {
  console.error(`读不到 ${file}: ${e.message}`)
  process.exit(2)
}

// 每个命令需要哪几道检查。
//   'both'  —— 操作某个窗口：急停 + 白名单都要
//   'halt'  —— 有副作用但不对具体窗口：只要急停
//   'none'  —— 纯粹的查询/状态管理，不碰任何东西
const POLICY = {
  CmdClick: 'both',
  CmdClickElement: 'both',
  CmdSetValue: 'both',
  CmdDoAction: 'both',
  CmdType: 'both',
  CmdPressKey: 'both',
  CmdTree: 'both',
  CmdScreenshot: 'both',
  // region_hash 抓屏幕上的像素：和 screenshot 同一级别（急停 + 白名单）。
  CmdRegionHash: 'both',
  // region_diff 同样抓屏 + 读 PNG：和 region_hash 一级。
  CmdRegionDiff: 'both',
  // image_hash 只读一张本地 PNG，不碰桌面；但急停时同样停 ——
  // 急停的含义是"别再用我的界面内容做任何事"。
  CmdImageHash: 'halt',
  CmdWhatIs: 'both',
  // a11y_probe 读的是界面内容（元素名字、角色、矩形）—— 和 whatis / tree
  // 同一级别，所以**两道守卫都要**：急停时停，白名单外的应用不读。
  CmdA11yProbe: 'both',
  // ps_read 起一个 PowerShell 子进程读 UIA 树 —— 读的是界面内容，
  // 和白名单外的应用一个级别，所以同样两道守卫都要。
  // 它还会**执行脚本**，虽然脚本由 helper 自己生成、不接受调用方传入，
  // 但"能启进程"这件事本身也不该在急停状态下发生。
  CmdPsRead: 'both',
  CmdHold: 'both',
  CmdDragPath: 'both',
  // release **刻意**既不检查急停也不检查白名单：
  // 松开是恢复动作，急停时用户最需要的正是把按着的东西松开。
  CmdRelease: 'none',
  CmdMove: 'both',
  CmdDrag: 'both',
  CmdWheel: 'both',
  CmdLaunch: 'halt',
  CmdResolveApp: 'none',
  CmdListApps: 'none',
  CmdListWindows: 'none',
  CmdAllowCheck: 'none',
  CmdHalt: 'none',
  CmdStop: 'none',
  CmdGateProbe: 'none',
  CmdPing: 'none',
  // cursor 只读光标位置，不注入、不读界面文字 —— 不需要任何守卫。
  CmdCursor: 'none',
  CmdHelp: 'none',
}

/**
 * 切出某个 Cmd 方法的函数体。
 *
 * 命令方法的签名有两种：`CmdX(string[] args)` 和 `CmdX()`（无参的
 * list_windows / ping / gate_probe / help）。第一版只匹配带参的那种，
 * 于是无参命令被当成"源码里不存在"——脚本自己报错了，而不是代码有问题。
 */
function bodyOf(src, name) {
  const re = new RegExp(`private static int ${name}\\((?:string\\[\\] args)?\\)`)
  const m = re.exec(src)
  if (!m) return null
  const rest = src.slice(m.index + m[0].length)
  const nxt = /\n        private static int Cmd/.exec(rest)
  return nxt ? rest.slice(0, nxt.index) : rest.slice(0, 4000)
}

const problems = []
const seen = new Set()

for (const m of src.matchAll(/private static int (Cmd\w+)\((?:string\[\] args)?\)/g)) {
  const name = m[1]
  seen.add(name)
  const want = POLICY[name]
  if (want === undefined) {
    problems.push(`${name}: 未在本脚本的 POLICY 表里分类（新增命令请归类，不要默认放过）`)
    continue
  }
  const body = bodyOf(src, name)
  if (body === null) {
    problems.push(`${name}: 找不到方法体`)
    continue
  }
  const hasHalt = body.includes('RequireNotHalted(')
  const hasAllow = body.includes('RequireAllowedWindow(')

  if ((want === 'both' || want === 'halt') && !hasHalt) {
    problems.push(`${name}: 缺急停检查 —— 闸门拉下后它还会照常执行`)
  }
  if (want === 'both' && !hasAllow) {
    problems.push(`${name}: 缺白名单检查 —— 不在名单内的应用也照做`)
  }
}

// 反过来：POLICY 里写了但源码里没有的命令，说明表过时了
for (const name of Object.keys(POLICY)) {
  if (!seen.has(name)) problems.push(`POLICY 表里的 ${name} 在源码里不存在（表已过时？）`)
}

if (problems.length === 0) {
  const both = Object.values(POLICY).filter((v) => v === 'both').length
  const halt = Object.values(POLICY).filter((v) => v === 'halt').length
  console.log(`  ✓ 全部 ${seen.size} 个命令的守卫齐全`)
  console.log(`      ${both} 个需急停+白名单，${halt} 个需急停，其余不需`)
  process.exit(0)
}

console.log('  ✗ helper 命令的守卫有缺口：')
for (const p of problems) console.log(`      ${p}`)
console.log('      这两道检查是逐命令手写的，漏掉不会报错——命令只是照常工作。')
process.exit(1)
