// 急停（ESC）留下的闸门，不能被任何"收尾"动作静默解除。
//
// 这两条是第三方复核发现的，而且都属于**注释说得对、代码做不到**：
//
//   ① desktop_off 先 halt 再 stop --clear。注释写着"ESC 留下的闸门保持不动"，
//      但 --clear 无条件删文件 —— 用户按了 ESC，助手一句 desktop_off
//      就把急停解除了。
//   ② gate_probe 用**真实闸门路径**做写→查→删。闸门此刻正拉下时，
//      这个"诊断"会覆盖原因再删掉它，还报告 healthy。
//      desktop_diagnose 每调一次就解除一次急停。
//
// 判据只有一条：**闸门在测试前后必须一模一样**（存在、内容、意思都不变）。
//
// 用法：node tools/test-gate-safety.mjs

import { readFileSync, existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { spawnSync, spawn } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const DSC = 'helper/bin/dsc.exe'
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'gate-safety-'))
const GATE = join(tmp, 'halt')

function run(args, env = {}) {
  const r = spawnSync(DSC, args, {
    encoding: 'utf8',
    env: { ...process.env, DSC_HALT_GATE: GATE, ...env },
    timeout: 20000,
  })
  let json = null
  try { json = JSON.parse((r.stdout ?? '').trim()) } catch { }
  return { json, stdout: r.stdout ?? '' }
}

const gateText = () => (existsSync(GATE) ? readFileSync(GATE, 'utf8').trim() : null)

console.log()
console.log('── 1. gate_probe 不许动正在生效的闸门 ──')

{
  run(['stop', '--clear'])
  // 模拟用户按 ESC：闸门内容写明原因
  spawnSync(DSC, ['halt', '用户按了 ESC'], {
    encoding: 'utf8',
    env: { ...process.env, DSC_HALT_GATE: GATE },
  })
  const before = gateText()
  if (!before) {
    bad('前置失败：没能拉下闸门')
  } else {
    ok(`闸门已拉下，原因：${before.slice(0, 20)}`)

    const r = run(['gate_probe'])
    const after = gateText()

    if (after === null) {
      bad('gate_probe 把闸门删掉了 —— 用户的急停被静默解除')
    } else if (after !== before) {
      bad(`gate_probe 改写了闸门内容：${JSON.stringify(after.slice(0, 30))}`)
    } else {
      ok('gate_probe 之后闸门原样保留（存在、内容都没变）')
    }

    if (r.json?.alreadyHalted === true) ok('gate_probe 如实报告"闸门当前拉下"')
    else bad(`alreadyHalted 应为 true，实际 ${r.json?.alreadyHalted}`)

    if (r.json?.stage === 'probe-only') ok('走的是"只探测目录、不碰闸门"的路径')
    else bad(`stage 应为 probe-only，实际 ${r.json?.stage}`)
  }
}

console.log()
console.log('── 2. 闸门不存在时，gate_probe 仍要能做完整测试 ──')

{
  run(['stop', '--clear'])
  if (gateText() !== null) bad('前置失败：闸门没清掉')
  else {
    const r = run(['gate_probe'])
    if (r.json?.healthy === true) ok('闸门不存在时做完整写→查→删测试，报告 healthy')
    else bad(`完整测试失败：${JSON.stringify(r.json)}`)
    if (gateText() === null) ok('测试后没有留下闸门')
    else bad('测试后留下了闸门')
  }
}

console.log()
console.log('── 3. desktop_off 不许解除用户的急停 ──')

{
  run(['stop', '--clear'])
  spawnSync(DSC, ['halt', '用户按了 ESC'], {
    encoding: 'utf8',
    env: { ...process.env, DSC_HALT_GATE: GATE },
  })
  const before = gateText()

  // 用一个最小 ctx 调 desktop_off
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
  const mod = await import(pathToFileURL('plugin/index.mjs').href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: DSC,
    escPath: 'helper/bin/dsc-esc.exe',
    gatePath: GATE,
    shotDir: join(tmp, 'shots'),
  })
  const off = reg.find((t) => t.name === 'desktop_off')
  const r = await off.execute({}).catch((e) => ({ error: e.message }))
  const after = gateText()

  if (after === null) {
    bad('desktop_off 删掉了闸门 —— 用户按 ESC 之后助手一收尾就解除了急停')
  } else if (after !== before) {
    bad(`desktop_off 改写了闸门内容：${JSON.stringify((after ?? '').slice(0, 30))}`)
  } else {
    ok('desktop_off 之后闸门原样保留')
  }

  const msg = r.result ?? r.error ?? ''
  if (/闸门仍处于拉下状态/.test(msg)) ok('desktop_off 明确告诉调用方"急停仍在生效"')
  else if (/没有替你解除/.test(msg)) ok('desktop_off 说明它没有替你解除')
  else bad(`desktop_off 的返回没提急停状态：${msg.slice(0, 80)}`)
}

console.log()
console.log('── 4. desktop_off 在闸门未拉下时也不该弄出闸门 ──')

{
  run(['stop', '--clear'])
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
  const mod = await import(pathToFileURL('plugin/index.mjs').href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: DSC,
    escPath: 'helper/bin/dsc-esc.exe',
    gatePath: GATE,
    shotDir: join(tmp, 'shots'),
  })
  const off = reg.find((t) => t.name === 'desktop_off')
  await off.execute({}).catch(() => {})
  if (gateText() === null) ok('desktop_off 没有留下闸门（正常关闭不该封锁后续操作）')
  else bad('desktop_off 弄出了一道闸门')
}

try { rmSync(tmp, { recursive: true, force: true }) } catch {}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
