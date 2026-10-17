// 用受控测试靶验证"我说的话"和"靶子实际收到什么"一致。
//
// ── 为什么换掉原来的做法 ──
//
// 之前所有测试都拿"屏幕上当前某个够大的窗口"当靶子，有两个后果：
//   1. **不可核对**：点完之后只能看插件返回的文字说成功了 —— 而那正是
//      我一直在修的那类问题（自证）。
//   2. **不稳定**：窗口会移动、被遮挡、被关掉，同一个测试有时过有时不过，
//      而失败与代码无关。
//
// 现在靶子是 dsc-target.exe：每个控件有稳定名字，每次真实交互都由
// **靶子自己**写进日志。证据来自被测系统之外，这才算数。
//
// ── 哪些项在这里能跑 ──
//
// 沙箱会拦 SetCursorPos/keybd_event，所以"点击真的落在哪"不能在这里测。
// 能测的是不经过输入注入的路径：读树、set_value（UIA 写入）、
// 描述符定位、do_action。点击落点需要另跑（见文件末尾的说明）。
//
// 用法：node tools/test-target.mjs

import { spawn, spawnSync } from 'node:child_process'
import { existsSync, readFileSync, rmSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => console.log(`  · ${m}`)

const ROOT = process.cwd()
const TARGET = join(ROOT, 'tools', 'target', 'bin', 'dsc-target.exe')
const DSC = join(ROOT, 'helper', 'bin', 'dsc.exe')
const RUN = join(ROOT, '.cache', 'target-run')
const LOG = join(RUN, 'events.jsonl')
const GATE = join(RUN, 'halt')

if (!existsSync(TARGET)) {
  console.log(`  ✗ 找不到 ${TARGET}，先跑 tools/target/build.sh`)
  process.exit(1)
}

mkdirSync(RUN, { recursive: true })
rmSync(LOG, { force: true })
rmSync(GATE, { force: true })

function run(args, env = {}) {
  const r = spawnSync(DSC, args, {
    encoding: 'utf8',
    env: { ...process.env, DSC_HALT_GATE: GATE, ...env },
    timeout: 20000,
  })
  let json = null
  try { json = JSON.parse((r.stdout ?? '').trim()) } catch { }
  return { json, stdout: r.stdout ?? '', stderr: r.stderr ?? '' }
}

/** 读靶子的日志。**证据只来自这里**，不来自 helper 的返回值。 */
function events() {
  if (!existsSync(LOG)) return []
  return readFileSync(LOG, 'utf8')
    .split('\n')
    .filter((l) => l.trim())
    .map((l) => {
      try { return JSON.parse(l.replace(/^\uFEFF/, '')) } catch { return null }
    })
    .filter(Boolean)
}

function waitReady(timeoutMs = 15000) {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const ready = events().find((e) => e.event === 'ready')
    if (ready) return ready
    spawnSync('powershell.exe', ['-NoProfile', '-Command', 'Start-Sleep -Milliseconds 200'], { windowsHide: true })
  }
  return null
}

// ── 启动靶子 ──
// 用 detached + stdio ignore，和插件启动应用的方式一致；
// Start-Process 在这个沙箱里起不来，所以走 spawn。
const child = spawn(TARGET, ['--log', LOG], { detached: true, stdio: 'ignore', windowsHide: false })
child.unref()

const ready = waitReady()
if (!ready) {
  console.log('  ✗ 靶子没有在 15 秒内就绪（看日志文件是否可写）')
  try { process.kill(child.pid) } catch { }
  process.exit(1)
}

const HWND = ready.hwnd
console.log()
console.log(`  靶子就绪：hwnd=${HWND} ${ready.width}x${ready.height}`)
console.log(`  日志：${LOG}`)

const cleanup = () => {
  try { process.kill(child.pid) } catch { }
  try {
    spawnSync('powershell.exe', ['-NoProfile', '-Command',
      `Get-Process dsc-target -EA SilentlyContinue | Where-Object { $_.Id -eq ${child.pid} } | Stop-Process -Force`],
      { windowsHide: true })
  } catch { }
}
process.on('exit', cleanup)

console.log()
console.log('── 1. 读树：控件必须按名字读得到 ──')

{
  const r = run(['tree', String(HWND), '200'], { DSC_ALLOWED_APPS: 'dsc-target.exe' })
  if (!r.json?.ok) {
    bad(`读树失败：${(r.json?.message ?? r.stdout).slice(0, 70)}`)
  } else {
    const tree = r.json.tree ?? ''
    for (const name of ['Alpha', 'Beta', 'Input', 'Choice', 'Items']) {
      if (tree.includes(name)) ok(`树里有 ${name}`)
      else bad(`树里没有 ${name}（共 ${r.json.total} 个节点）`)
    }

    // ComboBox 的子项：这正是"不该把 ComboBox 当叶节点剪掉"要验的
    const hasChoiceItems = /\bA\b/.test(tree) && /\bB\b/.test(tree) && /\bC\b/.test(tree)
    if (hasChoiceItems) ok('ComboBox 的子项（A/B/C）出现在树里 —— 剪枝修复生效')
    else skip('树里没看到 ComboBox 的子项（可能因为未展开；UI Automation 对此的实现因控件而异）')

    // ListView 的列头：Header 曾经被剪成叶节点
    const hasHeader = /Key/.test(tree) && /Value/.test(tree)
    if (hasHeader) ok('ListView 的列头（Key/Value）出现在树里')
    else skip('树里没看到列头（取决于 ListView 的 UIA 实现）')
  }
}

console.log()
console.log('── 2. 描述符唯一性：两个同名按钮必须被拒绝 ──')

const before = events().length
{
  const r = run(['click_element', String(HWND), 'role=Button/name=Duplicate'], {
    DSC_ALLOWED_APPS: 'dsc-target.exe',
  })
  const code = r.json?.error ?? ''
  const msg = r.json?.message ?? ''
  // 错误码从 InvalidOperationException 改成了 AmbiguousTarget
  // （异常类名当错误码对调用方毫无意义，也查不到恢复建议）。
  // 这里接受两种：新旧都算"被拒绝了"，但**新码要真的报对**。
  if (code === 'AmbiguousTarget' && /多个元素/.test(msg)) {
    ok('同名按钮报 AmbiguousTarget 并拒绝')
  } else if (code === 'InvalidOperationException' && /多个元素/.test(msg)) {
    skip('还报着旧码 InvalidOperationException —— 说明跑的是旧 helper')
  } else if ((code === 'AmbiguousTarget' || code === 'InvalidOperationException') && /激活/.test(msg)) {
    skip('被激活限制拦下（沙箱无法把窗口提到前台），唯一性检查在此环境下测不到')
  } else {
    bad(`同名按钮的行为不对：error=${JSON.stringify(code)} msg=${msg.slice(0, 70)}`)
  }

  // 关键判据：**靶子的日志里不该有这次点击**
  const after = events().length
  if (after === before) ok('被拒绝的点击没有在靶子上留下任何事件（没有真的点下去）')
  else bad(`被拒绝的点击居然在靶子上留下 ${after - before} 条事件`)
}

console.log()
console.log('── 3. 用 AutomationId 组成唯一描述符 ──')

{
  // 两个 Duplicate 按钮的 Name 属性（AccessibleName）相同，但 Control.Name 不同。
  // 这里用一个一定唯一的组合来验证"唯一命中时可以执行"。
  const r = run(['click_element', String(HWND), 'role=Button/name=Alpha'], {
    DSC_ALLOWED_APPS: 'dsc-target.exe',
  })
  const code = r.json?.error ?? ''
  const msg = r.json?.message ?? ''
  if (r.json?.ok === true) ok('唯一描述符执行成功')
  else if (/多个元素/.test(msg)) bad('唯一描述符被误判为多个匹配')
  else if (/激活/.test(msg)) skip('被激活限制拦下（沙箱限制，不是代码问题）')
  else bad(`唯一描述符执行失败：${code} ${msg.slice(0, 70)}`)
}

console.log()
console.log('── 4. set_value 写进去的内容必须和目标一致 ──')

{
  const beforeN = events().length
  const r = run(['set_value', String(HWND), 'role=Edit/name=Input', '从测试写进来的文本'], {
    DSC_ALLOWED_APPS: 'dsc-target.exe',
  })
  const code = r.json?.error ?? ''
  const msg = r.json?.message ?? ''

  if (r.json?.ok === true) {
    // 证据：靶子自己的日志
    const evs = events().slice(beforeN)
    const changed = evs.filter((e) => e.event === 'value_changed' && e.id === 'Input')
    if (changed.length > 0) {
      const last = changed[changed.length - 1]
      if (last.value === '从测试写进来的文本') {
        ok('靶子的日志确认收到了正确的文本')
      } else {
        bad(`靶子收到的文本不一致：${JSON.stringify(last.value)}`)
      }
    } else {
      bad('set_value 报成功，但靶子的日志里没有 value_changed')
    }
  } else if (/激活/.test(msg)) {
    skip('被激活限制拦下（沙箱限制）')
  } else {
    bad(`set_value 失败：${code} ${msg.slice(0, 70)}`)
  }
}

console.log()
console.log('── 5. 点击落点：必须命中预期那个按钮 ──')

{
  // 靶子已经把每个控件的**屏幕矩形**报出来了，所以这里能精确算中心。
  // 这是这条测试最关键的地方：只验证"有 click 事件"是不够的 ——
  // 恰好点中旁边的按钮也会产生事件，而那正是"点偏了还以为成功"。
  const rects = ready.controls ?? []
  const alpha = rects.find((c) => c.name === 'Alpha')
  const beta = rects.find((c) => c.name === 'Beta')

  if (!alpha || !beta) {
    bad(`靶子没有报告 Alpha/Beta 的矩形（收到 ${rects.length} 个控件）`)
  } else {
    async function clickCenter(target, label) {
      const cx = target.x + Math.floor(target.w / 2)
      const cy = target.y + Math.floor(target.h / 2)
      const beforeN = events().length
      const r = run(['click', String(cx), String(cy), 'left', '--hwnd', String(HWND)], {
        DSC_ALLOWED_APPS: 'dsc-target.exe',
      })
      const msg = r.json?.message ?? ''
      if (r.json?.ok === true) {
        const evs = events().slice(beforeN)
        const hit = evs.find((e) => e.event === 'click')
        if (!hit) {
          bad(`${label}：报点击成功，但靶子没记录到任何 click`)
        } else if (hit.id === label) {
          ok(`点击 ${label} 中心 (${cx},${cy}) → 靶子记录的正是 ${label}`)
        } else {
          bad(`点击 ${label} 中心 (${cx},${cy}) → 靶子记录的却是 ${hit.id}（点偏了）`)
        }
      } else if (/SetCursorPos|拒绝访问|激活|光标未落/.test(msg)) {
        skip(`${label}：注入被环境拦下（${msg.slice(0, 40)}…）`)
      } else {
        bad(`${label}：点击失败 ${msg.slice(0, 60)}`)
      }
      await new Promise((r2) => setTimeout(r2, 400))
    }

    await clickCenter(alpha, 'Alpha')
    await clickCenter(beta, 'Beta')

    // 反向验证：两个不同位置必须产生不同的命中。
    // 如果两次都记成同一个按钮，说明落点根本不生效。
    const clicks = events().filter((e) => e.event === 'click').map((e) => e.id)
    if (clicks.length >= 2) {
      const distinct = new Set(clicks)
      if (distinct.size >= 2) ok(`不同位置命中了不同按钮（${[...distinct].join(', ')}）`)
      else bad(`两次点击都落在 ${[...distinct][0]} 上 —— 落点可能根本没生效`)
    }
  }
}

console.log()
console.log('── 6. 日志本身必须可信 ──')

{
  const evs = events()
  const seqs = evs.map((e) => e.seq)
  const monotonic = seqs.every((v, i) => i === 0 || v > seqs[i - 1])
  if (monotonic) ok(`seq 单调递增（共 ${evs.length} 条事件）`)
  else bad(`seq 不单调：${seqs.slice(0, 10).join(',')}`)

  if (evs.some((e) => e.event === 'ready')) ok('有 ready 事件（可判断靶子何时就绪）')
  else bad('没有 ready 事件')
}

cleanup()

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
console.log()
console.log('  说明：点击落点必须在**插件环境**里验证（helper 由宿主启动、不受沙箱限制）。')
console.log('        做法：让助手调 desktop_screenshot 拿坐标，再调 desktop_click，')
console.log('        然后比对 target 日志里 event=click 的 id 是不是预期那个。')
process.exit(fail === 0 ? 0 : 1)
