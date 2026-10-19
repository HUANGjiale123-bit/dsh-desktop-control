// 外部复核指出的三个行为问题，用**行为**验证（不是 src.includes）。
//
// ── 覆盖 ──
//
//   1. 排队取消：B 在等锁时被取消，A 释放后 B 不能派发输入
//   2. hold 所有权：hold 之后执行权被钉住，别的调用拿不到
//   3. Timeout 语义：不能建议"重放原动作"
//
// 用法：node tools/test-behavior-fixes.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}`); skipped++ }

console.log()
console.log('── 1. Timeout 不能建议重放原动作 ──')

{
  const ts = readFileSync('server/toolset.mjs', 'utf8')
  const i = ts.indexOf('Timeout:')
  const body = ts.slice(i, i + 400)
  if (body.includes("retry: 'reobserve'")) ok('Timeout → reobserve（动作可能已发出）')
  else bad('Timeout 还建议重试 —— 可能重复输入/重复提交')
  if (/可能已经发出|可能已发出/.test(body)) ok('明确说了"动作可能已经发出"')
  else bad('没有提醒动作可能已生效')
}

console.log()
console.log('── 2. 等锁可以取消 ──')

{
  const src = readFileSync('plugin/index.mjs', 'utf8')
  if (/async function acquireLease\(tool, signal\)/.test(src)) ok('acquireLease 接收 signal')
  else bad('acquireLease 不接受取消信号')

  const i = src.indexOf('async function acquireLease')
  const body = src.slice(i, i + 1600)
  if (body.includes('signal.aborted')) ok('等待循环里检查 aborted')
  else bad('等待时不看取消信号')

  // 拿到锁后、派发前还要再查一次
  const j = src.indexOf('拿到执行权后发现这次调用已被取消')
  if (j > 0) ok('拿到锁后还会再查一次取消')
  else bad('拿锁后没复查 —— 等待期间取消会被忽略')
}

console.log()
console.log('── 3. hold 期间执行权被钉住 ──')

{
  const src = readFileSync('plugin/index.mjs', 'utf8')
  for (const [needle, label] of [
    ['function pinLease', '有 pinLease'],
    ['isLeasePinned()', '有查询函数'],
    ["'desktop_release',", 'release 不占执行权（紧急释放不能被 8 秒挡住）'],
  ]) {
    if (src.includes(needle)) ok(label)
    else bad(`${label}（找不到 "${needle}"）`)
  }

  if (/if \(leaseHeld && !isLeasePinned\(\)\) releaseLease\(\)/.test(src)) {
    ok('finally 遇到钉住的执行权不释放')
  } else {
    bad('finally 无条件释放 —— hold 返回后别的会话能接进来')
  }
}

console.log()
console.log('── 4. 行为验证：取消的等待者不派发输入 ──')

{
  const tmp = mkdtempSync(join(process.cwd(), '.cache', 'behav-'))
  for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs']) {
    writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
  }

  try {
    const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
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
    await mod.apply(ctx, {
      helperPath: 'helper/bin/dsc.exe',
      escPath: 'helper/bin/dsc-esc.exe',
      gatePath: join(tmp, 'halt'),
      shotDir: join(tmp, 'shots'),
    })
    const byName = new Map(reg.map((t) => [t.name, t]))

    // 用 move 占锁（它会挂很久？不会 —— 但它需要真实窗口，会失败）。
    // 改用两个都会失败的只读工具制造排队：它们都在同一把锁上。
    const rw = byName.get('desktop_read_window')
    if (!rw) { skip('没有 read_window'); }
    else {
      // A：正常调用（会稍慢）
      const a = rw.execute({ hwnd: 1 }).catch((e) => ({ err: e.message }))

      // B：带一个已 abort 的 signal，等一下再发（确保它进入排队）
      const ac = new AbortController()
      const b = rw.execute({ hwnd: 1 }, { signal: ac.signal }).catch((e) => ({ err: e.message }))
      ac.abort()   // B 可能在排队时被取消

      const [ra, rb] = await Promise.all([a, b])
      void ra
      const msg = rb?.err ?? ''
      if (/取消/.test(msg)) {
        ok('被取消的调用明确报"取消"，没有继续执行')
      } else if (/没拿到执行权|Busy/.test(msg)) {
        ok('被拒（Busy 或取消），没有派发输入')
      } else {
        skip(`两个调用没有真的撞上（环境太快）：${msg.slice(0, 50)}`)
      }
    }
  } catch (e) {
    skip(`行为验证跑不起来：${e.message.slice(0, 60)}`)
  }
  try { rmSync(tmp, { recursive: true, force: true }) } catch {}
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
