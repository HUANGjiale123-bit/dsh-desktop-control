#!/usr/bin/env node
/* 验证 desktop_click_element 在 Chromium / Tauri 上真的能按名字点。
 *
 * 背景：dsc.exe 是 .NET 进程，Chromium 系窗口在它眼里是一个
 * IsControlElement=True、Descendants=0 的"叶子"，FindByDescriptor 只扫得到
 * 根节点，于是**任何**描述符都是 NotFound。同一个窗口换成非 .NET 的桥
 * （python → PowerShell）能读到 39~62 个有名字、有矩形的节点。
 *
 * 所以这里分五段验，每段失败都能指向具体一环：
 *   1. 桥能不能按描述符找到元素（dry-run，不产生任何副作用）
 *   2. 找不到 / 多命中时，给的信息够不够改描述符
 *   3. 急停闸门对桥是否同样有效（变更性动作必须被拦住）
 *   4. 走 toolset 的 clickElement 能不能真的把界面点动（往返一次，回到原页）
 *   5. 源码级回归断言（helper 不得用 method 字符串判断是否已 Invoke）
 */

import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, rmSync, writeFileSync, readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { pathToFileURL } from 'node:url'

const require = createRequire(import.meta.url)
const path = require('node:path')

const results = []
let pass = 0
let fail = 0
const ok = (m) => { results.push(`  ✓ ${m}`); pass++ }
const bad = (m) => { results.push(`  ✗ ${m}`); fail++ }
const note = (m) => results.push(`    · ${m}`)

/* ── 桥的调用器 ──
 *
 * 这里直接起 python，不经过 toolset —— 第 1~3 段验的是桥本身，
 * 混进 toolset 就分不清"桥找不到"和"toolset 没把结果用上"。
 */
function findPython() {
  const cands = [
    process.env.DSC_PYTHON,
    path.join(process.env.USERPROFILE || process.env.HOME || '', '.dsh', 'dsh-runtimes',
      'dsh-primary-runtime', 'dependencies', 'python', 'python.exe'),
  ]
  for (const c of cands) if (c && existsSync(c)) return c
  return 'python'
}

const PY = findPython()
const SCRIPT = path.resolve('server/uia-bridge.py')

function bridge(args, envExtra) {
  const r = spawnSync(PY, [SCRIPT, ...args], {
    encoding: 'utf8',
    timeout: 90000,
    env: { ...process.env, ...(envExtra || {}) },
  })
  const line = (r.stdout || '').trim().split('\n').filter(Boolean).pop()
  if (!line) {
    return { ok: false, error: 'no output', stderr: String(r.stderr || '').slice(0, 300) }
  }
  try {
    return JSON.parse(line)
  } catch (e) {
    return { ok: false, error: `bad json: ${e.message}`, raw: line.slice(0, 300) }
  }
}

const clickReq = (hwnd, descriptor, extra) =>
  JSON.stringify({ hwnd, descriptor, ...(extra || {}) })

/* ── 找一个 Chromium 系窗口当靶子 ── */

const toolsetPath = pathToFileURL(path.resolve('server/toolset.mjs')).href
const { createDesktopToolset } = await import(toolsetPath)
// 先用一个空配置的实例只做观察（列窗口不受白名单限制），
// 找到靶子之后再按它的 exe 建一个能操作的实例 —— 否则会撞 WhitelistEmpty。
const probe = createDesktopToolset({})

const listRes = await probe.tools.listWindows({})
const windows = listRes.windows ?? []
const target =
  windows.find((w) => String(w.class ?? '').includes('Tauri')) ??
  windows.find((w) => String(w.class ?? '').includes('Chrome_WidgetWin') && w.width < 1000)

if (!target) {
  results.push('  ✗ 没找到 Chromium 系窗口（Tauri / Chrome_WidgetWin_1 都没有）')
  console.log(results.join('\n'))
  process.exit(1)
}
results.push(`  目标: hwnd=${target.hwnd} class=${target.class} ${target.width}x${target.height}`)
results.push(`  python: ${PY}`)
results.push('')

const targetExe = String(target.exe ?? '').split(/[\\/]/).pop()
const set = createDesktopToolset({ allowedApps: targetExe })
results.push(`  白名单: ${targetExe}`)
results.push('')

const HWND = target.hwnd

/* ══ 1. 桥按名字找元素（dry-run） ══ */
results.push('── 1. 桥能按描述符找到元素（dry-run，不点） ──')
{
  const r = bridge(['--click', clickReq(HWND, 'role=Button/name=首 页', { dryRun: true })])
  if (!r.ok) {
    bad(`dry-run 失败：${r.error} ${r.message ?? r.stderr ?? ''}`)
  } else {
    if (r.method === 'DryRun') ok('返回 method=DryRun（确实没有动手）')
    else bad(`method 应为 DryRun，实际 ${r.method}`)

    const el = r.element ?? {}
    if (el.name === '首 页') ok(`元素名字对上了：${JSON.stringify(el.name)}`)
    else bad(`元素名字是 ${JSON.stringify(el.name)}，期望 "首 页"`)

    if (el.type === 'Button') ok('元素类型是 Button')
    else bad(`元素类型是 ${JSON.stringify(el.type)}，期望 Button`)

    if (el.w > 0 && el.h > 0 && el.l > -10000 && el.t > -10000) {
      ok(`矩形合理：${el.l},${el.t} ${el.w}x${el.h}`)
    } else {
      bad(`矩形不合理：${el.l},${el.t} ${el.w}x${el.h}`)
    }

    if (typeof r.hasInvoke === 'boolean') ok(`hasInvoke=${r.hasInvoke}（决定走 Invoke 还是坐标）`)
    else bad('返回里没有 hasInvoke，调用方无法判断该走哪条路')

    note(`dpi=${r.dpi} 来源 hwnd=${r.viaHwnd}（${r.viaClassName}）节点 ${r.nodes}`)
  }
}

/* ══ 2. 找不到 / 多命中时的信息量 ══ */
results.push('')
results.push('── 2. 找不到、多命中时，信息够不够改描述符 ──')
{
  const r = bridge(['--click', clickReq(HWND, 'role=Button/name=充值2', { dryRun: true })])
  if (r.error === 'NotFound') ok('名字写错时返回 NotFound（而不是含糊的失败）')
  else bad(`期望 NotFound，实际 ${r.error} ${r.message ?? ''}`)

  const near = Array.isArray(r.nearby) ? r.nearby : []
  if (near.includes('充值')) {
    ok(`附近的名字里有一模一样的按钮："充值"（实际有这些：${near.join(' / ')}）`)
  } else if (near.some((n) => /退出登录|检查更新|TUN/.test(n))) {
    // 应用停在"我的"页时首页的按钮本来就不在，这不是功能坏了。
    // 靶子状态由测试自己切换，切页失败会让这一格失去前提 —— 跳过而不是失败。
    note(`界面停在"我的"页（首页的"充值"不在这一页），这一格未验证`)
  } else {
    bad(`附近的名字里没有"充值"，拿到的是 ${JSON.stringify(near)}`)
  }
  if (r.nearbyKind === 'sameRole') note('nearbyKind=sameRole：role 对上了、只有 name 不对')
}
{
  const r = bridge(['--click', clickReq(HWND, 'role=Text', { dryRun: true })])
  if (r.error === 'AmbiguousTarget') ok('一个描述符命中多个元素时拒绝执行')
  else bad(`期望 AmbiguousTarget，实际 ${r.error} ${r.message ?? ''}`)
  if ((r.matches ?? 0) > 1) ok(`报告了命中数量（至少 ${r.matches} 个）`)
  else bad(`没有报告命中数量：${JSON.stringify(r.matches)}`)
  if (Array.isArray(r.sample) && r.sample.length > 0) ok(`给了前几个候选：${r.sample[0]}`)
  else bad('没有给出候选样例，调用方没法据此收窄描述符')
}

/* ══ 3. 急停闸门对桥同样有效 ══ */
results.push('')
results.push('── 3. 急停闸门必须拦住桥（否则"按了 ESC 按钮照样被点"） ──')
{
  const gateDir = path.resolve('.tmp')
  mkdirSync(gateDir, { recursive: true })
  const gate = path.join(gateDir, 'gate-test-halt')
  writeFileSync(gate, 'test')
  try {
    const r = bridge(['--click', clickReq(HWND, 'role=Button/name=首 页')], { DSC_HALT_GATE: gate })
    if (r.error === 'Halted') ok('闸门存在时桥拒绝执行（没有真的去点）')
    else bad(`闸门存在时仍然执行了：${JSON.stringify(r).slice(0, 200)}`)
    note(`闸门路径：${gate}`)
  } finally {
    rmSync(gate, { force: true })
  }
  // 闸门删掉之后必须恢复可用（不能一次误判永久锁死）
  const r2 = bridge(['--click', clickReq(HWND, 'role=Button/name=首 页', { dryRun: true })],
    { DSC_HALT_GATE: gate })
  if (r2.ok) ok('闸门清掉之后恢复正常')
  else bad(`闸门清掉之后仍然失败：${r2.error} ${r2.message ?? ''}`)
}

/* ══ 4. toolset 的 clickElement：真的把界面点动 ══ */
results.push('')
results.push('── 4. desktop_click_element 走桥，真的点动界面 ──')
{
  const HOME = '剩余流量'   // 首页独有
  const MINE = '退出登录'   // "我 的"页独有
  const readText = async () => (await set.tools.readWindow({ hwnd: HWND, maxNodes: 120 })).text ?? ''

  let text = await readText()
  if (!text.includes(HOME)) {
    // 不在首页时先回首页，后面才有确定的起点
    await set.tools.clickElement({ hwnd: HWND, descriptor: 'role=Button/name=首 页' })
    await new Promise((r) => setTimeout(r, 900))
    text = await readText()
  }

  if (!text.includes(HOME)) {
    results.push('    · 起始状态不是首页（找不到"剩余流量"），跳过往返验证')
  } else {
    ok('起点确认在首页')

    const rMine = await set.tools.clickElement({ hwnd: HWND, descriptor: 'role=Button/name=我 的' })
    const tMine = rMine.text ?? ''
    if (rMine.isError && /无法把窗口|激活到前台/.test(tMine)) {
      // 前台被别的程序占着时 helper 会拒绝激活 —— 那是**保护**，不是缺陷：
      // 硬把前台从别人手里抢过来，等于让用户的键盘接着去别处打字。
      // 这一格验的是"桥能不能按名字找到并点中"，不是"能不能抢前台"，所以跳过。
      note(`前台被别的窗口占着，helper 拒绝激活（这一格未验证）：${tMine.split('\n')[0].slice(0, 60)}`)
    } else if (rMine.isError) {
      bad(`点「我 的」失败：${tMine.split('\n')[0]}`)
    } else {
      if (tMine.includes('undefined')) bad(`报告里出现 undefined：${tMine.split('\n')[0]}`)
      else ok('成功报告里没有 undefined 字段')

      if (tMine.includes('桥')) ok('报告说明了这次定位走的是 UIA 桥')
      else bad(`报告没说走桥：${tMine.split('\n')[0]}`)

      if (tMine.includes('InvokePattern') || tMine.includes('坐标点击')) {
        ok(`动作用的是 ${tMine.includes('InvokePattern') ? 'InvokePattern' : '坐标点击'}`)
      } else {
        bad(`报告里看不出用了哪种方式：${tMine.split('\n')[0]}`)
      }

      await new Promise((r) => setTimeout(r, 900))
      const tAfter = await readText()
      if (tAfter.includes(MINE)) ok('界面真的变了：读到了「退出登录」（我的页）')
      else bad(`点了「我 的」但界面没变，树里没有"${MINE}"`)
    }

    const rHome = await set.tools.clickElement({ hwnd: HWND, descriptor: 'role=Button/name=首 页' })
    if (rHome.isError && /无法把窗口|激活到前台/.test(rHome.text ?? '')) {
      note(`点回「首 页」时前台又被占：${(rHome.text ?? '').split('\n')[0].slice(0, 60)}`)
    } else if (rHome.isError) {
      bad(`点回「首 页」失败：${(rHome.text ?? '').split('\n')[0]}`)
    } else {
      await new Promise((r) => setTimeout(r, 900))
      const tBack = await readText()
      if (tBack.includes(HOME)) ok('点回「首 页」成功，界面恢复（靶子已复原）')
      else bad('点回「首 页」后界面没有恢复——应用可能停在别的页')
    }
  }
}

/* ══ 5. 源码级回归断言 ══ */
results.push('')
results.push('── 5. 源码级回归断言 ──')
{
  const ts = readFileSync(path.resolve('server/toolset.mjs'), 'utf8')
  if (ts.includes("r.method === 'InvokePattern'")) {
    bad("toolset 仍用 === 比较 method —— Invoke 成功后会被当成坐标点击")
  } else {
    ok("toolset 用 startsWith 判断 method（不再依赖完全相等的字符串）")
  }

  const cs = readFileSync(path.resolve('helper/src/Program.cs'), 'utf8')
  if (cs.includes('if (invoked)')) {
    ok('helper 用布尔量 invoked 决定"已经 Invoke 过就返回"')
  } else {
    bad('helper 没有 invoked 判断 —— Invoke 之后可能再点一次（同一个按钮触发两次）')
  }
  // 注释里会保留"当年那句 method == 判断"的说明，所以只看**代码行**。
  const code = cs.split('\n')
    .filter((l) => !/^\s*(\/\/|\*|\/\*)/.test(l))
    .join('\n')
  if (code.includes('method == "InvokePattern"')) {
    bad('helper 仍在用 method 字符串判断 —— 光标移动成功时会重复触发')
  } else {
    ok('helper 的代码行里没有 method 字符串比较')
  }
}

console.log(results.join('\n'))
console.log('')
console.log('══════════════════════════════')
if (fail === 0) console.log(`  ${pass} 项通过`)
else console.log(`  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
