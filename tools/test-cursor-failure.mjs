#!/usr/bin/env node
/* 回归：光标装饰/看门狗**启动失败**时，宿主必须活着。
 *
 * 背景（复核 R2）：spawn() 之后只监听了 exit，没监听 error。启动失败
 * （ENOENT/EACCES/文件被移走）是**异步** 'error' 事件，没有监听者就是未处理事件，
 * 整个 Node 宿主直接退出。修复前，这个测试会让本进程以非零码退出。
 *
 * 做法：照常加载插件、开启装饰，但**故意不提供 dsc-fx.exe**，
 * 让 spawn 走真实的 ENOENT；然后断言宿主仍在、工具仍可调用。
 *
 * 用法：node tools/test-cursor-failure.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, copyFileSync, rmSync, existsSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

const HELPER = tempHelper()
assertNotLow(HELPER)

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'fail-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}
mkdirSync(join(tmp, 'bin'), { recursive: true })
// 只放主 helper，**故意不放 dsc-fx.exe / dsc-guard.exe** —— spawn 会 ENOENT
copyFileSync(join('helper', 'bin', 'dsc.exe'), join(tmp, 'bin', 'dsc.exe'))

// 素材必须齐：否则 start() 在"素材缺失"处提前返回，根本走不到 spawn 边界
// （复核 2.18.71 P2 指出的正是这一点）
mkdirSync(join(tmp, 'assets', 'cursor', 'poses-arrow'), { recursive: true })
copyFileSync(join('plugin', 'assets', 'cursor', 'fish-arrow.png'), join(tmp, 'assets', 'cursor', 'fish-arrow.png'))
copyFileSync(join('plugin', 'assets', 'cursor', 'poses-arrow', 'index.json'),
  join(tmp, 'assets', 'cursor', 'poses-arrow', 'index.json'))
for (const f of readdirSync(join('plugin', 'assets', 'cursor', 'poses-arrow'))) {
  if (f.endsWith('.png')) {
    copyFileSync(join('plugin', 'assets', 'cursor', 'poses-arrow', f),
      join(tmp, 'assets', 'cursor', 'poses-arrow', f))
  }
}

const reg = []
const warns = []
const ctx = {
  logger: { info: () => {}, warn: (m) => warns.push(String(m)), error: () => {} },
  tools: { register: (t) => { reg.push(t); return () => {} } },
  on: () => {},
  get() { return undefined },
}

const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: HELPER,
  escPath: 'helper/bin/dsc-esc.exe',
  shotDir: join(tmp, 'shots'),
  cursorFx: true,
  cursorFxDir: join(tmp, 'fx'),
  // 注入一个**确定不存在**的路径：stage() 不再提前返回，spawn 会真的 ENOENT
  cursorFxExePath: join(tmp, 'does-not-exist', 'dsc-fx.exe'),
  cursorFxGuardPath: join(tmp, 'does-not-exist', 'dsc-guard.exe'),
  allowSingleSession: true,   // 这个测试不测会话，只测子进程失败隔离
})

const tool = reg.find((t) => t.name === 'desktop_list_windows')
if (!tool) {
  console.log('  ✗ 插件没有注册 desktop_list_windows')
  process.exit(1)
}

console.log()
console.log('── 1. 装饰启动失败（dsc-fx.exe 不存在）──')
let firstOk = false
try {
  const out = await tool.execute({}, {})
  const text0 = JSON.stringify(out).slice(0, 400)
  firstOk = /窗口|window/i.test(text0)
  if (!firstOk) console.log('    返回内容：' + text0.slice(0, 200))
} catch (e) {
  console.log(`    调用抛错：${String(e?.message ?? e).slice(0, 100)}`)
}
// error 是**异步**事件：等它到达再断言"确实尝试过启动"
await new Promise((r) => setTimeout(r, 600))

// 能走到这一行就说明宿主没被未处理事件带走
ok('宿主仍然存活（未因未处理的 error 事件退出）')
const spawnWarns = warns.filter((m) => /启动失败/.test(m))
if (spawnWarns.length >= 1) ok(`确实尝试过启动并收到错误（${spawnWarns.length} 条告警）`)
else bad('没有任何"启动失败"告警 —— 测试可能没走到 spawn 边界')
if (spawnWarns.some((m) => /ENOENT|spawn/i.test(m))) ok('错误内容来自真实的启动失败（ENOENT）')
else console.log(`  · 告警原文：${spawnWarns.map((m) => m.slice(0, 80)).join(' / ')}`)
if (firstOk) ok('工具本身照常返回结果（装饰失败不影响核心功能）')
else bad('工具调用失败 —— 装饰失败不应影响核心工具')

console.log()
console.log('── 2. 连续调用：不能陷入"每次都重启失败进程"的循环 ──')
const warnsBefore = warns.length
for (let i = 0; i < 3; i++) {
  try { await tool.execute({}, {}) } catch { }
  await new Promise((r) => setTimeout(r, 300))
}
const newWarns = warns.length - warnsBefore
if (newWarns === 0) ok('冷却生效：后续调用没有再尝试启动（没有重复告警）')
else console.log(`  · 后续调用又产生了 ${newWarns} 条告警`)

console.log()
console.log('── 3. 诊断仍可用 ──')
const diag = reg.find((t) => t.name === 'desktop_diagnose')
if (diag) {
  try {
    const out = await diag.execute({}, {})
    const text = String(out?.result ?? out?.text ?? '')
    if (/版本/.test(text)) ok('desktop_diagnose 可调用')
    else bad('diagnose 返回异常')
  } catch (e) {
    bad(`diagnose 抛错：${String(e?.message ?? e).slice(0, 80)}`)
  }
} else {
  bad('desktop_diagnose 未注册')
}

try { rmSync(tmp, { recursive: true, force: true }) } catch { }

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
