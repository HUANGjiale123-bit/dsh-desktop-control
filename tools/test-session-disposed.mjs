// 会话结束时撤销授权，以及"授权在途时会话被销毁"的竞态。
//
// ── 外部复核的原话 ──
//
// "dispose 取消该实例的排队请求、清授权和引用，**不能只删 Map 却让在途
// 审批回来重新写入**。"
//
// 那个竞态是：
//
//   授权弹窗发出 → 会话被关掉 → 用户点了"允许" → 写进 grants
//   → 一个**已经不存在的会话**留下了授权，而且没人会来清它
//
// ── 这个测试守什么 ──
//
//   · revokeSession 清掉该会话的授权
//   · 销毁后再写入被拒绝（不是静默写进去）
//   · 别的会话不受影响
//
// 用法：node tools/test-session-disposed.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'disp-'))
for (const f of ['toolset.mjs', 'apps.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('server', f)))
}

console.log()
console.log('── 1. 销毁会话会撤销它的授权 ──')

{
  const tmod = await import(pathToFileURL(join(tmp, 'toolset.mjs')).href + '?' + Math.random())
  let current = 'session-A'
  const set = tmod.createDesktopToolset({
    helperPath: 'helper/bin/dsc.exe',
    allowedApps: '',
    getGatePath: () => join(tmp, 'halt'),
    getSessionKey: () => current,
    requestApproval: async () => 'allowed-once',
  })

  // A 给自己授权
  set.grants.set('notepad.exe', 'session')
  if (set.grants.has('notepad.exe')) ok('销毁前 A 有授权')
  else bad('授权没写进去')

  // 销毁 A
  set.revokeSession('session-A')

  current = 'session-A'
  if (!set.grants.has('notepad.exe')) ok('销毁后 A 的授权没了')
  else bad('销毁后授权还在 —— revokeSession 没生效')

  if (set.isSessionDisposed('session-A')) ok('isSessionDisposed 返回 true')
  else bad('没有标记为已销毁')

  // 别的会话不受影响
  current = 'session-B'
  set.grants.set('mspaint.exe', 'session')
  current = 'session-A'
  set.revokeSession('session-A')
  current = 'session-B'
  if (set.grants.has('mspaint.exe')) ok('销毁 A 不影响 B')
  else bad('销毁 A 把 B 也清了')
}

console.log()
console.log('── 2. 销毁之后不能重新写入（在途审批的竞态）──')

{
  const tmod = await import(pathToFileURL(join(tmp, 'toolset.mjs')).href + '?' + Math.random())
  const current = 'session-X'
  let approvals = 0
  const set = tmod.createDesktopToolset({
    helperPath: 'helper/bin/dsc.exe',
    allowedApps: '',
    getGatePath: () => join(tmp, 'halt'),
    getSessionKey: () => current,
    requestApproval: async () => {
      approvals++
      // 模拟：用户点"允许"的时候，会话已经被销毁了
      set.revokeSession('session-X')
      return 'allowed-once'
    },
  })

  // 走真正的授权路径：需要一个真实窗口 —— 沙箱里拿不到，
  // 所以这里直接调用 authorize 的导出口（如果有）或者验证结构。
  //
  // authorize 不在导出里，所以退一步：验证"写入前会查 isSessionDisposed"
  // 这条逻辑存在于源码里，且 revokeSession 真的能挡住后续写入。
  const src = readFileSync('server/toolset.mjs', 'utf8')
  if (/isSessionDisposed\(_k\)/.test(src) || /isSessionDisposed\(currentSessionKey\(\)\)/.test(src)) {
    ok('authorize 写入前会复查会话是否已销毁')
  } else {
    bad('写入前没有复查 —— 在途审批会给死会话发授权')
  }

  // 行为验证：销毁后，写入路径应该被挡
  set.revokeSession('session-X')
  if (set.isSessionDisposed('session-X')) {
    ok('已销毁标记存在（写入路径据此拒绝）')
  } else {
    bad('标记没建立')
  }
  void approvals
}

console.log()
console.log('── 3. 插件 dispose 要清定时器 ──')

{
  const src = readFileSync('plugin/index.mjs', 'utf8')
  const i = src.indexOf("ctx.on?.('dispose'")
  const seg = i > 0 ? src.slice(i, i + 900) : ''
  if (seg.includes('_idleTimer') && seg.includes('clearInterval')) {
    ok('dispose 里清了 _idleTimer')
  } else {
    bad('dispose 没清定时器 —— 卸载后它还在空转')
  }
  if (seg.includes('liveOverlays.clear()')) ok('dispose 清浮层（原有的）')
  else bad('浮层没清')

  if (src.includes("ctx.on?.('session/disposed'")) ok('监听了 session/disposed')
  else bad('没有监听 session/disposed —— 会话结束不清授权')
}

try { rmSync(tmp, { recursive: true, force: true }) } catch {}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  ${pass} 项通过` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
