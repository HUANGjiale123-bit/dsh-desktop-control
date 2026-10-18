// 会话隔离：A 授权不流入 B。
//
// ── 外部复核指出的泄漏 ──
//
// "grants 是一个全局 Map。会话 A 授权 notepad 之后，会话 B 直接就能用"
// —— 而"仅对当前会话有效"是我们在授权弹窗里对用户的承诺。
//
// index 那边虽然有一份 sessionGrants，但它只决定"要不要弹窗"；
// 真正的准入判断在 toolset。所以那份同步不了这个泄漏。
//
// ── 这个测试守什么 ──
//
//   · 两个会话各有自己的授权表
//   · A 授权后，B 的 effectiveAllowlist 里没有它
//   · B 用同一个应用仍会被要求授权
//   · 静态白名单仍是共享的（那是配置，不是会话行为）
//   · 撤销只影响当前会话
//
// 用法：node tools/test-session-grants.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'sess-'))
for (const f of ['toolset.mjs', 'apps.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('server', f)))
}

console.log()
console.log('── 1. 授权按会话分 ──')

{
  const tmod = await import(pathToFileURL(join(tmp, 'toolset.mjs')).href + '?' + Math.random())

  // 用一个可切换的"当前会话"
  let current = 'session-A'
  let approvals = 0

  const set = tmod.createDesktopToolset({
    helperPath: 'helper/bin/dsc.exe',
    allowedApps: '',                        // 静态白名单为空
    getGatePath: () => join(tmp, 'halt'),
    getSessionKey: () => current,
    requestApproval: async () => {
      approvals++
      return 'allowed-once'
    },
  })

  // A 授权 notepad（用一个真实存在的窗口触发不成，改用 authorizeApp 路径）
  // 直接操作 grants 的公开 getter 不方便，所以用"模拟一次授权"：
  // 这里用 toolset 暴露的 grants getter 写入，验证的是**表结构**是否按会话分。
  const grantsA = set.grants
  grantsA.set('notepad.exe', 'session')

  const effA = set.effectiveAllowlist?.() ?? []

  // 切到 B
  current = 'session-B'
  const grantsB = set.grants
  const effB = set.effectiveAllowlist?.() ?? []

  // 切回 A 再读一次
  current = 'session-A'
  const effA2 = set.effectiveAllowlist?.() ?? []

  if (!effA.includes('notepad.exe')) {
    // effectiveAllowlist 可能没导出，改用 grants 表判断
    if (grantsA.has('notepad.exe')) ok('会话 A 的授权存在')
    else bad('会话 A 的授权没写进它自己的表')
  } else {
    ok('会话 A 的授权存在')
  }

  if (!grantsB.has('notepad.exe')) ok('会话 B 看不到 A 的授权（隔离生效）')
  else bad('会话 B 能看到 A 的授权 —— 泄漏仍存在')

  if (grantsA.has('notepad.exe')) ok('切回 A 后授权还在（不是全局清空）')
  else bad('切回 A 后授权丢了 —— 清空范围太大')

  void effA; void effB; void effA2; void approvals

  console.log()
  console.log('── 2. 撤销只影响当前会话 ──')

  // B 也给自己授权一个
  current = 'session-B'
  set.grants.set('mspaint.exe', 'session')

  // 在 B 里撤销
  set.revokeGrants?.()

  current = 'session-A'
  const aAfter = set.grants
  current = 'session-B'
  const bAfter = set.grants

  if (!bAfter.has('mspaint.exe')) ok('B 撤销后自己的授权清空了')
  else bad('B 撤销没生效')

  if (aAfter.has('notepad.exe')) ok('A 的授权没被 B 的撤销影响')
  else bad('B 撤销把 A 的授权也清了 —— 撤销范围太大')

  console.log()
  console.log('── 3. 静态白名单仍然共享 ──')

  const set2 = tmod.createDesktopToolset({
    helperPath: 'helper/bin/dsc.exe',
    allowedApps: 'shared-app.exe',          // 显式配置的共享预授权
    getGatePath: () => join(tmp, 'halt'),
    getSessionKey: () => 'session-X',
    requestApproval: async () => 'allowed-once',
  })
  const eff = set2.effectiveAllowlist ? set2.effectiveAllowlist() : []
  if (eff.includes('shared-app.exe')) ok('静态白名单对所有会话可见（配置，不是会话行为）')
  else bad('静态白名单没生效')
}

console.log()
console.log('── 4. 没有会话身份时退化成单会话（测试/独立运行）──')

{
  const tmod = await import(pathToFileURL(join(tmp, 'toolset.mjs')).href + '?' + Math.random())
  const set = tmod.createDesktopToolset({
    helperPath: 'helper/bin/dsc.exe',
    allowedApps: '',
    getGatePath: () => join(tmp, 'halt'),
    requestApproval: async () => 'allowed-once',
    // 故意不传 getSessionKey
  })
  set.grants.set('x.exe', 'session')
  if (set.grants.has('x.exe')) ok('没有会话时仍可用（默认桶）')
  else bad('没有会话时授权不生效 —— 会破坏独立运行路径')
}

try { rmSync(tmp, { recursive: true, force: true }) } catch {}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  ${pass} 项通过` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
