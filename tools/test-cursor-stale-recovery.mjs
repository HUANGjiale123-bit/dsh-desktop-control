#!/usr/bin/env node
/* 回归：启动时恢复"上一实例被同时强杀"的残留状态，且**只在确认成功时**清理。
 *
 * 背景（2.18.79 复核 P1）：旧实现用 spawnAsync，它失败/超时/空输出都 resolve，
 * 于是恢复失败也删标记、还写"已恢复"。现在要求：
 *   · 只有 { ok:true, data:{ ok:true } } 才算确认
 *   · 失败/未知 → 保留标记，记"未确认"，下次再试
 *   · 有待恢复标记的旧目录不能被 GC 提前删掉
 *
 * 本测试**不碰真实 %TEMP%、不碰系统光标**：状态目录用 config.cursorFxBaseDir 指到
 * 私有目录，恢复调用用 config.cursorRestore 替身。
 *
 * 用法：node tools/test-cursor-stale-recovery.mjs
 */

import { existsSync, mkdirSync, rmSync, writeFileSync, utimesSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const ROOT = join(process.cwd(), '.cache', 'stale-recovery-test')

let seq = 0
/** 加载一份新插件实例；返回 { warns, infos, restoreCalls }。 */
async function applyWith({ base, restore, enabled = false }) {
  mkdirSync(base, { recursive: true })
  const warns = []
  const infos = []
  const state = { calls: 0 }
  const reg = []
  const ctx = {
    logger: {
      info: (m) => infos.push(String(m)),
      warn: (m) => warns.push(String(m)),
      error: () => {},
    },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    on: () => {},
    get() { return undefined },
  }
  const mod = await import(pathToFileURL(join(process.cwd(), 'plugin', 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: HELPER,
    escPath: 'helper/bin/dsc-esc.exe',
    shotDir: join(ROOT, 'shots'),
    allowSingleSession: true,
    cursorFxBaseDir: base,
    ...(enabled ? { cursorFx: true, cursorFxExePath: join(base, 'missing', 'dsc-fx.exe') } : {}),
    cursorRestore: async () => { state.calls++; return restore() },
  })
  return { warns, infos, state, reg, mod }
}

const makeDeadDir = (base, name, pid = 999999) => {
  const d = join(base, name)
  mkdirSync(d, { recursive: true })
  writeFileSync(join(d, 'fx.blanked'), String(pid))
  return d
}

try {
  rmSync(ROOT, { recursive: true, force: true })
  mkdirSync(ROOT, { recursive: true })

  console.log()
  console.log('── 1. 恢复返回 ok:false：必须保留标记并报"未确认" ──')
  {
    const base = join(ROOT, 'b1')
    const d = makeDeadDir(base, '111')
    const t = await applyWith({ base, restore: async () => ({ ok: true, data: { ok: false } }) })
    await sleep(1200)
    if (existsSync(join(d, 'fx.blanked'))) ok('标记被保留')
    else bad('失败时把标记删了（旧缺陷）')
    if (t.warns.some((w) => /未确认/.test(w))) ok('日志明确写"未确认"')
    else bad('失败时没有"未确认"告警')
    if (!t.infos.some((i) => /已确认恢复/.test(i))) ok('没有写"已恢复"的成功日志')
    else bad('失败时仍报告成功')
  }

  console.log()
  console.log('── 2. helper 非零退出 / 空响应：同样保留 ──')
  for (const [label, ret] of [
    ['非零退出', { ok: false, error: 'exit', code: 1 }],
    ['空对象', { ok: true, data: {} }],
  ]) {
    const base = join(ROOT, 'b2-' + label)
    const d = makeDeadDir(base, '222')
    const t = await applyWith({ base, restore: async () => ret })
    await sleep(1000)
    if (existsSync(join(d, 'fx.blanked'))) ok(`${label}：标记保留`)
    else bad(`${label}：标记被删`)
    if (!t.infos.some((i) => /已确认恢复/.test(i))) ok(`${label}：没有谎报成功`)
    else bad(`${label}：谎报成功`)
  }

  console.log()
  console.log('── 3. 明确 ok:true 才清理 ──')
  {
    const base = join(ROOT, 'b3')
    const d = makeDeadDir(base, '333')
    const t = await applyWith({ base, restore: async () => ({ ok: true, data: { ok: true } }) })
    await sleep(1200)
    if (!existsSync(d)) ok('确认成功后目录被清理')
    else bad('确认成功后目录还在')
    if (t.infos.some((i) => /已确认恢复/.test(i))) ok('写了成功日志')
    else bad('没有成功日志')
  }

  console.log()
  console.log('── 4. 活着的实例不动，也不触发恢复 ──')
  {
    const base = join(ROOT, 'b4')
    const d = makeDeadDir(base, '444', process.pid)
    const t = await applyWith({ base, restore: async () => ({ ok: true, data: { ok: true } }) })
    await sleep(800)
    if (existsSync(join(d, 'fx.blanked'))) ok('活实例的标记没被动')
    else bad('活实例的标记被删了')
    if (t.state.calls === 0) ok('没有调用恢复')
    else bad('对活实例也调用了恢复')
  }

  console.log()
  console.log('── 5. GC 不能删带待恢复标记的旧目录 ──')
  {
    const base = join(ROOT, 'b5')
    const d = makeDeadDir(base, '555')
    // 把目录和文件时间改成 2 小时前
    const old = Date.now() / 1000 - 7200
    utimesSync(join(d, 'fx.blanked'), old, old)
    utimesSync(d, old, old)

    const t = await applyWith({
      base,
      restore: async () => ({ ok: true, data: { ok: false } }),   // 恢复永远失败
      enabled: true,                                            // 触发 bash()/GC
    })
    // 调一次桌面工具：会走 touch() → bash() → GC
    const tool = t.reg.find((x) => x.name === 'desktop_list_windows')
    try { await tool.execute({}, {}) } catch { }
    await sleep(800)
    if (existsSync(join(d, 'fx.blanked'))) ok('GC 跳过了待恢复目录')
    else bad('GC 在恢复完成前把待恢复状态删了')
  }
} finally {
  await sleep(300)
  try { rmSync(ROOT, { recursive: true, force: true }) } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
