// 审查报告修复项的回归测试：click 目标绑定、窗口枚举复用、set_value 描述符、
// 以及无障碍树不该剪掉的控件类型。
//
// 这几条都不是崩溃，而是"静默做错事"：
//   · click 不带目标 → 目标被遮挡时去点遮挡者，还报成功
//   · click 列两次窗口 → 每次多一个子进程（约 110ms）
//   · set_value 用遍历序号 → 树一变就写到别的控件上
//   · ComboBox/Header 被当叶节点 → 读树缺内容，且缺得毫无提示
//
// 用法：node tools/test-review-fixes.mjs

import { spawnSync } from 'node:child_process'
import { readFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const DSC = 'helper/bin/dsc.exe'
// 闸门放工作区内（沙箱子进程只能写那里）
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'review-test-'))
const GATE = join(tmp, 'halt')

function run(args, env = {}) {
  const r = spawnSync(DSC, args, {
    encoding: 'utf8',
    env: { ...process.env, DSC_HALT_GATE: GATE, ...env },
    timeout: 20000,
  })
  let json = null
  try { json = JSON.parse((r.stdout ?? '').trim()) } catch { }
  return { json, stdout: r.stdout ?? '', stderr: r.stderr ?? '', code: r.status }
}

console.log()

// 拿一些真实窗口
const lw = run(['list_windows'])
const wins = (lw.json?.windows ?? []).filter((w) => w.width > 200 && w.height > 200 && w.exe)
const target = wins.find((w) => !w.foreground) ?? wins[0]

if (!target) {
  console.log('  ✗ 找不到可用窗口，无法测试')
  process.exit(1)
}
const exeOf = (w) => String(w.exe).split(/[\\/]/).pop().toLowerCase()
const cx = target.x + Math.floor(target.width / 2)
const cy = target.y + Math.floor(target.height / 2)
console.log(`  测试窗口 hwnd=${target.hwnd} ${exeOf(target)} 中心 (${cx},${cy})`)

console.log()
console.log('── 1. click 必须接受并绑定目标窗口（--hwnd）──')

{
  // ① 传一个不在白名单的 hwnd：应当在**授权阶段**就拒绝，
  //    而且错误信息要提到白名单 —— 证明 --hwnd 被真的解析了。
  const other = wins.find((w) => w.hwnd !== target.hwnd) ?? target
  const r = run(['click', String(cx), String(cy), 'left', '--hwnd', String(other.hwnd)], {
    DSC_ALLOWED_APPS: 'definitely-not-this-app.exe',
  })
  const msg = r.json?.message ?? ''
  if (r.json?.ok === false) ok('指定 --hwnd 时被拒绝（没有静默去点别人）')
  else bad('指定 --hwnd 时仍然点了')

  if (/白名单/.test(msg) || /激活/.test(msg)) {
    ok('拒绝理由来自目标窗口校验（说明 --hwnd 被解析了）')
  } else {
    bad(`拒绝理由看不出与目标有关：${msg.slice(0, 80)}`)
  }

  // ② 不传 --hwnd 时行为不变（向后兼容）
  const r2 = run(['click', String(cx), String(cy), 'left'], {
    DSC_ALLOWED_APPS: 'definitely-not-this-app.exe',
  })
  if (r2.json?.ok === false) ok('不传 --hwnd 时仍然按落点校验（向后兼容）')
  else bad('不传 --hwnd 时行为变了')
}

console.log()
console.log('── 2. set_value 必须接受描述符，而不是只认遍历序号 ──')

{
  // 报错应当是"未找到"或"激活失败"，**不能是 FormatException**
  // —— FormatException 说明它把 role=Edit/... 当成数字去 parse 了。
  const r = run(['set_value', String(target.hwnd), 'role=Edit/name=绝对不存在的控件xyz', 'hello'], {
    DSC_ALLOWED_APPS: exeOf(target),
  })
  if (r.json?.error === 'FormatException') {
    bad('描述符被当成数字解析了 —— 说明没支持描述符模式')
  } else {
    ok(`描述符被正确识别（错误是 ${r.json?.error}，不是 FormatException）`)
  }

  // 序号模式仍然可用：越界应报 StaleSnapshot，而不是崩
  const r2 = run(['set_value', String(target.hwnd), '99999', 'hello'], {
    DSC_ALLOWED_APPS: exeOf(target),
  })
  if (r2.json?.error === 'StaleSnapshot' || r2.json?.error === 'InvalidOperationException') {
    ok('序号模式仍然工作（越界走明确的错误码）')
  } else {
    bad(`序号模式行为异常：${JSON.stringify(r2.json?.error)}`)
  }
}

console.log()
console.log('── 3. 无障碍树不该把 ComboBox / Header 当叶节点 ──')

{
  const src = readFileSync('helper/src/Snapshot.cs', 'utf8')
  // 只看 IsLeafType 函数体
  const i = src.indexOf('private static bool IsLeafType')
  const j = src.indexOf('/// <summary>', i + 10)
  const body = src.slice(i, j > 0 ? j : i + 3000)

  for (const t of ['ControlType.ComboBox', 'ControlType.Header', 'ControlType.HeaderItem']) {
    if (body.includes(`case "${t}"`)) {
      bad(`${t} 仍被当作叶节点 —— 它下面的内容读不到`)
    } else {
      ok(`${t} 不再被剪掉`)
    }
  }
}

console.log()
console.log('── 4. click 不该列两次窗口（每次多一个子进程）──')

{
  const src = readFileSync('server/toolset.mjs', 'utf8')
  // authorize 要支持复用；click 要把 pre.windows 传进去
  if (/opts\.windows/.test(src)) ok('authorize 支持复用调用方已列出的窗口')
  else bad('authorize 仍自己列窗口 —— 无法复用')

  if (/authorize\(hwnd, 'click', \{ windows:/.test(src)) {
    ok('click 把已列出的窗口传给 authorize')
  } else {
    bad('click 仍会重复列窗口')
  }
}

// 清理
try { rmSync(tmp, { recursive: true, force: true }) } catch { }

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
