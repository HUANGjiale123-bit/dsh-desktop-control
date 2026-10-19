// 改变布局的动作必须让旧截图的坐标立即失效。
//
// ── 为什么这条最要紧 ──
//
// 滚轮让内容滚动了，那张截图上的**所有**坐标就都指向了别的东西。
// 如果不作废，旧 shotId 会**通过全部检查**：
//   窗口没移 ✓  没改大小 ✓  没过期 ✓
// 然后把点击送到一个完全不同的位置。
//
// 校验通过了但结论是错的 —— 这比没有校验更危险，因为它给了虚假的确定性。
//
// 用法：node tools/test-shot-invalidate.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
// 跳过**不计入通过**：一个没被验证的断言算成绿色，比失败更误导。
let skipped = 0
const skip = (m) => { console.log(`  · ${m}`); skipped++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'shotinv-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

const reg = []
const ctx = {
  logger: { info: () => {}, warn: () => {}, error: () => {} },
  tools: { register: (t) => { reg.push(t); return () => {} } },
  get(n) {
    if (n === 'approval') return { request: async () => 'allowed-once' }
    if (n === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
    if (n === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['s1'] }] }
    if (n === 'attachments') return { saveImages: async () => [{ id: 'a' }] }
    return undefined
  },
}
const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: 'helper/bin/dsc.exe',
  escPath: 'helper/bin/dsc-esc.exe',
  gatePath: join(tmp, 'halt'),
  shotDir: join(tmp, 'shots'),
})

const byName = new Map(reg.map((t) => [t.name, t]))
const shot = byName.get('desktop_screenshot')
const click = byName.get('desktop_click')
const wheel = byName.get('desktop_wheel')

const { execFileSync } = await import('node:child_process')
const raw = JSON.parse(
  execFileSync('helper/bin/dsc.exe', ['list_windows'], { encoding: 'utf8', windowsHide: true }),
)
const ours = (w) => /^(dsc|dsc-esc|TextInputHost)\.exe$/i.test(String(w.exe ?? '').split(/[\\/]/).pop())
const usable = (w) => w.width > 300 && w.height > 300 && w.exe && !ours(w)
const win = raw.windows.find((w) => w.foreground && usable(w)) ?? raw.windows.find(usable)
if (!win) {
  console.log('  ✗ 找不到可用窗口')
  process.exit(1)
}

console.log()
console.log(`  测试窗口 hwnd=${win.hwnd}`)

console.log()
console.log('── 1. 截图能拿到 shotId ──')

let shotId = ''
{
  const r = await shot.execute({ hwnd: win.hwnd }).catch((e) => ({ err: e.message }))
  if (r.err) {
    console.log(`  （跳过：${r.err.split('\n')[0].slice(0, 60)}）`)
  } else {
    shotId = r.shotId ?? ''
    if (shotId) ok(`拿到 shotId：${shotId}`)
    else bad('没拿到 shotId')
  }
}

console.log()
console.log('── 2. 滚轮之后，这个 shotId 必须失效 ──')

if (!shotId) {
  console.log('  （跳过：没有 shotId）')
} else {
  const wr = await wheel
    .execute({ hwnd: win.hwnd, notches: -2, observe: false })
    .catch((e) => ({ err: e.message }))
  if (wr.err && !/SetCursorPos|拒绝访问|激活/.test(wr.err)) {
    bad(`滚轮执行失败：${wr.err.slice(0, 70)}`)
  } else {
    // 用那个旧 shotId 点一下，应当被拒绝
    const r = await click
      .execute({ hwnd: win.hwnd, x: 5, y: 5, space: 'window', shotId, observe: false })
      .catch((e) => ({ err: e.message }))
    const msg = r.err ?? r.result ?? ''
    if (/找不到截图/.test(msg)) {
      ok('滚轮之后旧 shotId 被拒绝（内容滚动导致坐标失效）')
    } else if (/SetCursorPos|拒绝访问|激活/.test(msg)) {
      // 注意这里**不能用 ok()**。上一版把它算成通过，于是"滚轮后作废"
      // 这条根本没被验证却显示全绿 —— 那正是"断言太宽松"的老毛病。
      skip(`注入被环境拦下，这条**未验证**：${msg.slice(0, 60)}`)
    } else {
      bad(`旧 shotId 仍然被接受 —— 滚轮后坐标已经失效却没人发现：${msg.slice(0, 80)}`)
    }
  }
}

console.log()
console.log('── 3. 新截图不受影响（作废的是旧的，不是全部）──')

{
  const r1 = await shot.execute({ hwnd: win.hwnd }).catch(() => ({ err: 'x' }))
  const fresh = r1.shotId ?? ''
  if (fresh && fresh !== shotId) {
    ok(`新截图有新的 shotId：${fresh.slice(-12)}`)
    // 这个新的应当可用（走完校验）
    const r = await click
      .execute({ hwnd: win.hwnd, x: 5, y: 5, space: 'window', shotId: fresh, observe: false })
      .catch((e) => ({ err: e.message }))
    const msg = r.err ?? r.result ?? ''
    if (/找不到截图/.test(msg)) {
      bad('新截图的 shotId 也被拒绝了 —— 作废范围搞错了')
    } else {
      ok('新截图的 shotId 能通过 shotId 校验（未被误伤）')
    }
  } else if (r1.err) {
    console.log('  （跳过：截图失败）')
  } else {
    bad('新截图没有产生不同的 shotId')
  }
}

try { rmSync(tmp, { recursive: true, force: true }) } catch {}

console.log()
console.log('── 4. 作废必须统一在执行器，而且覆盖全部变更性工具 ──')

// 这节原先检查"三个动作调用了作废"，而它们其实是手写的三处 ——
// 恰恰是问题本身：click / press_key / set_value / do_action / move / hold
// 一个都没有作废。现在统一到 register 的 finally 里，这里改成检查那份名单。
{
  const src = readFileSync('plugin/index.mjs', 'utf8')
  if (/function invalidateShotsFor/.test(src)) ok('invalidateShotsFor 存在')
  else bad('没有 invalidateShotsFor')

  // 必须是**统一在 wrapper**，不是散在各工具
  // 作废的位置变了：不再是"按 hwnd 无差别删"，而是"记下动作前的快照、
  // 只在 finally 里删那些"。原因见外部复核：无差别删会把 observeAfter
  // 刚登记的新截图一起删掉，让返回给模型的 shotId 立刻失效。
  if (src.includes('snapshotShotIds(hwndKey)') && src.includes('forgetShotIds(staleShots)')) {
    ok('作废统一在 register 的 finally 里，且只删动作前的快照')
  } else {
    bad('作废没有统一在注册器，或者仍在无差别清空（会删掉刚返回的新截图）')
  }

  // 提取 MUTATING_TOOLS 名单
  const m = src.match(/const MUTATING_TOOLS = new Set\(\[([\s\S]*?)\]\)/)
  const listed = new Set((m?.[1] ?? '').match(/'[a-z_]+'/g)?.map((x) => x.slice(1, -1)) ?? [])

  // 每个变更性工具都必须在名单里
  const mustHave = [
    'desktop_click', 'desktop_click_element', 'desktop_drag', 'desktop_type',
    'desktop_press_key', 'desktop_set_value', 'desktop_do_action',
    'desktop_wheel', 'desktop_move', 'desktop_hold', 'desktop_launch_app',
  ]
  const missing = mustHave.filter((t) => !listed.has(t))
  if (missing.length === 0) ok(`名单覆盖全部 ${mustHave.length} 个变更性工具`)
  else bad(`名单缺：${missing.join(', ')}`)

  // 只读工具不该在名单里
  const mustNot = ['desktop_screenshot', 'desktop_read_window', 'desktop_list_windows', 'desktop_whatis', 'desktop_diagnose']
  const wrong = mustNot.filter((t) => listed.has(t))
  if (wrong.length === 0) ok('只读工具不在变更名单里')
  else bad(`只读工具被当成变更性：${wrong.join(', ')}`)

  // 不允许残留的手写作废（那正是要消灭的东西）
  const handWritten = (src.match(/invalidateShotsFor\(args\.hwnd\)/g) ?? []).length
  if (handWritten === 0) ok('没有残留的手写作废调用')
  else bad(`还有 ${handWritten} 处手写作废 —— 应该全部交给执行器`)
}

console.log()
console.log('══════════════════════════════')
console.log(
  fail === 0
    ? `  ${pass} 项通过` + (skipped ? `，${skipped} 项跳过（未验证）` : '')
    : `  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过  ← 先修`,
)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
