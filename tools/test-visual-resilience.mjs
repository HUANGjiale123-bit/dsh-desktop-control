#!/usr/bin/env node
/* 验收第 7 条：视觉后端的失败必须是**有界**的 —— 不挂、不崩、不拖垮插件。
 *
 * 要验的四种坏情况（正常机器上复现不出来，所以用两个测试钩子逼出来）：
 *
 *   1. 脚本不见了        → VisualBackendUnavailable，其余工具照常
 *   2. 解释器不见了      → 同上（DSC_POWERSHELL 指向不存在的程序）
 *   3. 输出是垃圾        → BadJson，不是抛栈
 *   4. 卡死              → 超时返回，且**耗时有上限**（不能把插件挂在那儿）
 *
 * 另外两条结构性断言：
 *   5. 上面每一种情况之后，`desktop_visual_candidates` 返回的是**一段说明**，
 *      不是异常穿透到宿主 —— 模型看到"这条路不可用"，而不是整个插件报错；
 *   6. `desktop_list_windows` 仍然可用（证明插件整体没被拖垮）。
 *
 * 用法：node tools/test-visual-resilience.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`); skipped++ }

const HELPER = tempHelper()
assertNotLow(HELPER)

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-resilience-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

/* 起一个插件实例。env 直接进 process.env 会影响 ocr-worker 的模块级常量，
 * 所以每个场景各起一个 node 子进程？—— 不行，那太重。
 *
 * 这里换个更简单也更诚实的办法：每个场景改 process.env 之后**重新 import**
 * 一个带查询串的 index.mjs 副本，让 ocr-worker 以新的 env 重新求值。
 */
async function freshPlugin(tag) {
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
  // 每个场景复制一份目录，让模块缓存彻底隔离
  const dir = join(tmp, tag)
  mkdirSync(dir, { recursive: true })
  for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
    'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
    writeFileSync(join(dir, f), readFileSync(join('plugin', f)))
  }
  const mod = await import(pathToFileURL(join(dir, 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, { helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots') })
  return { by: new Map(reg.map((t) => [t.name, t])), dir }
}

const wins = (() => {
  try {
    return JSON.parse(spawnSync(HELPER, ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
  } catch { return [] }
})()
const win = wins.find((w) => String(w.class ?? '').includes('Tauri'))

const baseEnv = { ...process.env }
const scenarios = []

try {
  console.log()
  if (!win) {
    console.log('  ✗ 没找到 SuYou 窗口')
    process.exit(1)
  }

  /* 1. 脚本不见了 */
  scenarios.push({
    name: '脚本不见了（NoOcrScript）',
    env: { DSC_OCR_SCRIPT: join(tmp, 'definitely-missing.ps1') },
    expect: /不可用|NoOcrScript|失败/,
  })

  /* 2. 解释器不见了 */
  scenarios.push({
    name: '解释器不见了（指向不存在的 exe）',
    env: { DSC_POWERSHELL: join(tmp, 'no-such-powershell.exe') },
    expect: /不可用|Spawn|失败/,
  })

  /* 3. 输出是垃圾：让 PowerShell 跑一个只会打印垃圾的脚本 */
  {
    const junk = join(tmp, 'junk.ps1')
    // 假脚本也要认 -ProbeOnly：真脚本自己会提前返回，不认的话探测阶段就要睡满。
    writeFileSync(junk, [
      'param([switch]$ProbeOnly)',
      'if ($ProbeOnly) { Write-Output \'{"ok":true,"languages":["zh-Hans-CN"]}\'; exit 0 }',
      "Write-Output 'this is not json at all'",
    ].join('\n'))
    scenarios.push({
      name: '输出不是 JSON（协议不符）',
      env: { DSC_OCR_SCRIPT: junk },
      expect: /BadJson|不可用|失败/,
    })
  }

  /* 4. 卡死：脚本睡 5 秒，worker 的 timeout 设 800ms */
  {
    const slow = join(tmp, 'slow.ps1')
    writeFileSync(slow, [
      'param([switch]$ProbeOnly)',
      'if ($ProbeOnly) { Write-Output \'{"ok":true,"languages":["zh-Hans-CN"]}\'; exit 0 }',
      'Start-Sleep -Seconds 5',
      "Write-Output '{}'",
    ].join('\n'))
    scenarios.push({
      name: '卡死（5 秒脚本，800ms 超时）',
      env: { DSC_OCR_SCRIPT: slow, DSC_OCR_TIMEOUT_MS: '800' },
      expect: /Timeout|TIMEDOUT|超时|不可用/,
      maxMs: 4000,
    })
  }

  for (const sc of scenarios) {
    console.log(`── ${sc.name} ──`)
    process.env = { ...baseEnv, ...sc.env }
    const { by } = await freshPlugin('sc-' + Math.random().toString(36).slice(2, 8))

    const shotTool = by.get('desktop_screenshot')
    const candTool = by.get('desktop_visual_candidates')
    const listTool = by.get('desktop_list_windows')

    const shot = await shotTool.execute({ hwnd: win.hwnd })
    const t0 = Date.now()
    let text = ''
    try {
      const r = await candTool.execute({ hwnd: win.hwnd, shotId: shot.shotId })
      text = r.result ?? ''
    } catch (e) {
      text = 'THREW: ' + e.message
    }
    const ms = Date.now() - t0

    if (sc.expect.test(text)) ok(`返回可读说明（${text.split('\n')[0].slice(0, 60)}）`)
    else bad(`返回不符合预期：${text.slice(0, 160)}`)

    if (!/THREW/.test(text)) ok('没有异常穿透到宿主（是一条说明，不是抛栈）')
    else bad(`异常穿透了：${text.slice(0, 120)}`)

    if (sc.maxMs && ms > sc.maxMs) bad(`耗时 ${ms}ms 超过上限 ${sc.maxMs}ms —— 后台卡死会拖垮整个工具`)
    else if (sc.maxMs) ok(`有界失败：${ms}ms 内返回`)

    // 插件整体仍然可用
    const lw = await listTool.execute({})
    const lwText = lw.summary ?? lw.result ?? ''
    if (/窗口|hwnd/i.test(lwText)) ok('desktop_list_windows 仍然可用（插件没被拖垮）')
    else bad(`插件被拖垮了：${lwText.slice(0, 120)}`)
    console.log()
  }
} finally {
  process.env = baseEnv
  rmSync(tmp, { recursive: true, force: true })
}

console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
