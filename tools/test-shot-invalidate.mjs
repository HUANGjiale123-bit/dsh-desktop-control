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
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs']) {
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
console.log('── 4. 静态核对：三个动作确实调用了作废函数 ──')

{
  const src = readFileSync('plugin/index.mjs', 'utf8')
  if (/function invalidateShotsFor/.test(src)) ok('invalidateShotsFor 存在')
  else bad('没有 invalidateShotsFor')

  for (const [name, pat] of [
    ['desktop_wheel', /set\.tools\.wheel[\s\S]{0,400}?invalidateShotsFor/],
    ['desktop_drag', /set\.tools\.drag[\s\S]{0,400}?invalidateShotsFor/],
    ['desktop_type', /set\.tools\.type[\s\S]{0,400}?invalidateShotsFor/],
  ]) {
    if (pat.test(src)) ok(`${name} 在执行后调用了作废`)
    else bad(`${name} 没有调用作废 —— 布局变了但旧坐标仍然有效`)
  }
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
