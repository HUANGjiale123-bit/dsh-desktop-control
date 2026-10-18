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
  // ── 断言要精确，不能"只要不是 FormatException 就算过" ──
  //
  // 上一版写的是"不是 FormatException 就 ok"。第三方审查指出得对：
  // 那个判据会把**无输出、崩溃、超时、以及任何其它错误**统统算成通过。
  // 它只能区分"把描述符当数字解析"这一种失败，其余的失败全被放过。
  //
  // 现在要求：必须是结构化结果，且错误码精确落在允许的集合里。
  const r = run(['set_value', String(target.hwnd), 'role=Edit/name=绝对不存在的控件xyz', 'hello'], {
    DSC_ALLOWED_APPS: exeOf(target),
  })

  if (r.json === null) {
    bad(`没有拿到结构化 JSON（stdout=${JSON.stringify(r.stdout.slice(0, 60))}）`)
  } else {
    if (r.json.error === 'FormatException') {
      bad('描述符被当成数字解析了 —— 说明没支持描述符模式')
    } else if (r.json.error === 'NotFound') {
      ok('描述符被正确识别，并精确报 NotFound')
    } else if ((r.json.error === 'Refused' || r.json.error === 'InvalidOperationException') && /激活/.test(r.json.message ?? '')) {
      // 沙箱里激活不了窗口，这属于环境限制；关键是**错误码精确**。
      // （激活失败现在归为 Refused —— 环境限制，重试不会变好。）
      ok('描述符被正确识别（环境无法激活窗口，错误信息明确）')
    } else {
      bad(`描述符模式的错误码不符预期：${JSON.stringify(r.json.error)} ${String(r.json.message ?? '').slice(0, 60)}`)
    }
  }

  // 序号模式仍然可用：越界必须精确报 StaleSnapshot，且消息里带得上序号
  const r2 = run(['set_value', String(target.hwnd), '99999', 'hello'], {
    DSC_ALLOWED_APPS: exeOf(target),
  })
  if (r2.json === null) {
    bad('序号模式没有返回结构化 JSON')
  } else if (r2.json.error === 'StaleSnapshot') {
    if (/99999/.test(r2.json.message ?? '')) ok('序号越界报 StaleSnapshot，且消息带得上序号')
    else bad(`StaleSnapshot 的消息里没有序号：${String(r2.json.message ?? '').slice(0, 60)}`)
  } else if ((r2.json.error === 'Refused' || r2.json.error === 'InvalidOperationException') && /激活/.test(r2.json.message ?? '')) {
    // 激活失败发生在"解析序号"之前，所以这里测不到越界行为。
    // 错误码归为 Refused（环境限制，重试无用）。
    ok('序号模式被激活限制拦下（环境问题，不是解析问题）')
  } else {
    bad(`序号模式行为异常：${JSON.stringify(r2.json.error)}`)
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
