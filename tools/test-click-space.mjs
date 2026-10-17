// click 的坐标空间与验证信息回归测试。
//
// 这一块出过的实测事故（第三方报告 缺陷 4）：
//   把 desktop_screenshot 给的**图内坐标** (719,866) 直接传给 desktop_click，
//   于是点在了桌面 (719,866) —— 恰好落在另一个按钮上弹出菜单，
//   而工具返回"已在 (719,866) 执行点击"，**看起来完全成功**。
//
// 两处修复：
//   ① 加 space="window"，让插件自己加窗口原点
//   ② 返回里回显最终落到的桌面坐标 + 光标是否到位 + 落点属于哪个应用
//
// 这个测试**不需要真的点击成功**：它验证的是坐标换算与拒绝时的提示，
// 而点击本身在沙箱里会被系统拒绝（SetCursorPos 返回 False）。
//
// 用法：node tools/test-click-space.mjs

import { createDesktopToolset } from '../server/toolset.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const set = createDesktopToolset({
  helperPath: 'helper/bin/dsc.exe',
  allowedApps: '',
  getGatePath: () => '',
  requestApproval: async () => true,
})

console.log()

// 找一个够大的窗口来测（不需要它在前台，超范围分支不会真点击）
const wins = await set.tools.listWindows()
if (wins.isError) {
  console.log(`  ✗ listWindows 失败：${wins.text}`)
  process.exit(1)
}

// listWindows 返回的是文本，所以要再用 helper 拿结构化数据
const { execFileSync } = await import('node:child_process')
let raw = null
try {
  raw = JSON.parse(
    execFileSync('helper/bin/dsc.exe', ['list_windows'], { encoding: 'utf8', windowsHide: true }),
  )
} catch { }

// 本插件自己的窗口（dsc-esc 的全屏光带、逃生条）不该被当成"要操作的目标"。
// 它们的尺寸满足"够大的窗口"，但 dsc-esc 不在白名单里，选中它们必然被拒，
// 而报出来的错会指向完全无关的地方。
const isOurWindow = (w) => /^(dsc|dsc-esc)[.]exe$/i.test(String(w.exe ?? '').split(/[\/]/).pop())
const big = (raw?.windows ?? []).find(
  (w) => w.width > 400 && w.height > 400 && w.exe && !isOurWindow(w),
)
if (!big) {
  console.log('  ✗ 找不到可用于测试的窗口')
  process.exit(1)
}

const exe = String(big.exe).split(/[\\/]/).pop().toLowerCase()
console.log(`  测试窗口：hwnd=${big.hwnd} ${exe} 位于 (${big.x},${big.y}) 尺寸 ${big.width}x${big.height}`)

console.log()
console.log('── 1. space="window" 时超出窗口尺寸 → 拒绝，并给出换算结果 ──')

{
  const outX = big.width + 500
  const outY = big.height + 500
  const r = await set.tools.click({ hwnd: big.hwnd, x: outX, y: outY, space: 'window' })
  const expectDesktop = `(${big.x + outX},${big.y + outY})`
  if (r.isError) ok('超出图内尺寸被拒绝')
  else bad('超出图内尺寸却没有被拒绝')

  if (r.text.includes(expectDesktop)) {
    ok(`错误消息里给出了换算后的桌面坐标 ${expectDesktop}`)
  } else {
    bad(`错误消息里没有换算后的桌面坐标（期望含 ${expectDesktop}）`)
  }
  if (r.text.includes('图内坐标')) ok('说明了这是图内坐标超范围，而不是桌面坐标')
  else bad('没有区分"图内超范围"和"桌面坐标不在窗口内"')
}

console.log()
console.log('── 2. space="desktop"（缺省）时坐标不在窗口内 → 拒绝，并提示可用 space="window" ──')

{
  // 拿一个明显在窗口外的桌面坐标
  const r = await set.tools.click({ hwnd: big.hwnd, x: 1, y: 1 })
  // 如果窗口原点就是 0,0 附近，这个点可能恰好在窗口内；那种情况跳过
  const inside = 1 >= big.x && 1 < big.x + big.width && 1 >= big.y && 1 < big.y + big.height
  if (inside) {
    console.log('  （跳过：窗口覆盖了 (1,1)，找不到确定的窗外点）')
  } else {
    if (r.isError) ok('窗外坐标被拒绝')
    else bad('窗外坐标没有被拒绝')
    if (r.text.includes('桌面绝对坐标')) ok('说明了它按桌面绝对坐标解释')
    else bad('没有说明坐标空间')
    if (r.text.includes('space')) ok('提示了可以用 space="window"')
    else bad('没有提示 space 选项 —— 使用者还会再踩一次')
  }
}

console.log()
console.log('── 3. 两种空间都不该触发授权询问（范围检查在授权之前）──')

{
  let asked = 0
  const set2 = createDesktopToolset({
    helperPath: 'helper/bin/dsc.exe',
    allowedApps: '',
    getGatePath: () => '',
    requestApproval: async () => { asked++; return true },
  })
  await set2.tools.click({ hwnd: big.hwnd, x: big.width + 999, y: big.height + 999, space: 'window' })
  if (asked === 0) ok('坐标超范围时没有弹授权询问')
  else bad(`坐标超范围却问了 ${asked} 次授权 —— 白问`)
}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
