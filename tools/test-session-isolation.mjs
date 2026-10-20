#!/usr/bin/env node
/* 回归：授权必须按**会话**隔离，从真实注册的工具入口验证。
 *
 * 背景（复核 R1）：execute 入口只往 ALS 塞了 trace，没塞 agent，
 * 于是 getSessionKey() 恒为空，所有会话落进 '__default__' 授权表：
 *   A 授权后 B 不询问就能读；A 结束后授权也不失效。
 *
 * 这个测试不测 Map，而是加载插件、拿到注册的 desktop_list_windows，
 * 用宿主给的 exec.agent 分别扮演 A / B / A 结束，检查审批调用次数。
 *
 * 用法：node tools/test-session-isolation.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'sess-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

// 审批桩：记录"每次谁被问了"，不真弹窗
const approvals = []
const approvalsMock = {
  // 真实签名是 requestApproval(base, toolName)；测试只关心"问了几次"
  async request(base) { approvals.push(String(base)); return 'allowed-once' },
}
const disposeHandlers = []
const reg = []
const ctx = {
  logger: { info: () => {}, warn: () => {}, error: () => {} },
  tools: { register: (t) => { reg.push(t); return () => {} } },
  on: (ev, fn) => { (ctx.__handlers ??= {})[ev] = fn; if (ev === 'dispose') disposeHandlers.push(fn) },
  get(n) {
    if (n === 'approval') return approvalsMock
    if (n === 'agents') return { currentInitiator: () => null }
    if (n === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['sA', 'sB'] }] }
    return undefined
  },
}

const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots'),
  // 会话身份必须由宿主传：这里模拟宿主把 agent 塞进 exec
})

// 读窗口内容才会走授权（list_windows 不涉及目标窗口，本来就不问）
// 用真实的记事本当目标：不能拿"自己的窗口"测授权（会走 self 分支）
import { spawn } from 'node:child_process'
const np = spawn('C:/Windows/System32/notepad.exe', [], { stdio: 'ignore' })
await new Promise((r) => setTimeout(r, 1500))
const lw = JSON.parse(spawnSync(HELPER, ['list_windows'], { encoding: 'utf8', windowsHide: true }).stdout)
let target = (lw.windows ?? []).find((w) => (w.exe ?? '').toLowerCase().includes('notepad') && w.width > 200)
if (!target) {
  try { np.kill() } catch { }
  console.log('记事本窗口没出现，无法测试授权')
  process.exit(0)
}

const tool = () => {
  const t = reg.find((x) => x.name === 'desktop_read_window')
  if (!t) throw new Error('desktop_read_window 没有注册')
  return t
}

const callAs = async (agent) => {
  try {
    const out = await tool().execute({ hwnd: target.hwnd, maxNodes: 20 }, { agent })
    return { ok: true, text: String(out?.result ?? out?.text ?? '').slice(0, 60) }
  } catch (e) {
    return { ok: false, text: String(e?.message ?? e).slice(0, 120) }
  }
}

try {
  const A = { id: 'sA', session: { id: 'sA' } }
  const B = { id: 'sB', session: { id: 'sB' } }

  console.log()
  console.log('── 1. A 调用：应当询问一次 ──')
  const r1 = await callAs(A)
  if (approvals.length === 1) ok(`A 被询问（共 ${approvals.length} 次）`)
  else bad(`A 的审批次数异常：${approvals.length}`)
  if (r1.ok) ok('A 能读（授权后）')
  else bad(`A 读取失败：${r1.text}`)

  console.log()
  console.log('── 2. B 调用：必须重新询问，不能复用 A 的授权 ──')
  const before = approvals.length
  const r2 = await callAs(B)
  const askedB = approvals.length > before
  if (askedB) ok(`B 被单独询问（共 ${approvals.length} 次）`)
  else bad('B 没有经过询问就拿到授权 —— 授权串会话')

  console.log()
  console.log('── 3. A 会话结束后：A 的授权必须失效 ──')
  // 真的触发一次 session/disposed：插件必须据此撤销 A 的授权
  try { ctx.__handlers?.['session/disposed']?.({ id: 'sA' }) } catch { }
  // 先让 A 通过一次（可能已在第 1 步授权），然后结束 A，再调 A。
  const beforeA2 = approvals.length
  const r3 = await callAs(A)
  if (approvals.length > beforeA2) ok('A 结束后再调用会重新询问（授权已撤销）')
  else bad('A 结束后授权仍然有效 —— 撤销没有生效')

  console.log()
  console.log('── 4. 没有会话身份：必须受控拒绝 ──')
  const r4 = await callAs(null)
  if (!r4.ok && /会话身份|Refused/.test(r4.text)) ok('无身份时拒绝，且是受控错误')
  else bad(`无身份时没有拒绝：ok=${r4.ok} ${r4.text}`)
} finally {
  for (const fn of disposeHandlers) { try { await fn() } catch { } }
  try { rmSync(tmp, { recursive: true, force: true }) } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
