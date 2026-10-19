#!/usr/bin/env node
/* 验证 readWindow 在 Chromium 应用上走桥拿到内容。
 *
 * 这是整条链的端到端验证：
 *   toolset.readWindow → authorize → dsc.exe tree（不可用）
 *   → 判定不可用 → readTreeViaBridge（python + PowerShell）
 *   → 转成同样的形状 → 返回给调用方
 */

import { createRequire } from 'node:module'
import { pathToFileURL } from 'node:url'

const require = createRequire(import.meta.url)
const path = require('node:path')

const results = []
let pass = 0
let fail = 0
const ok = (m) => { results.push(`  ✓ ${m}`); pass++ }
const bad = (m) => { results.push(`  ✗ ${m}`); fail++ }

const toolsetPath = pathToFileURL(path.resolve('server/toolset.mjs')).href
const { createDesktopToolset } = await import(toolsetPath)
const probe = createDesktopToolset({})

// 找一个 SuYou 或者任何 Chromium 系窗口
const listRes = await probe.tools.listWindows({})
const windows = listRes.windows ?? []
// 优先 Tauri（已知桥能读到 39 个），其次别的 Chromium 系
const target =
  windows.find((w) => String(w.class ?? '').includes('Tauri')) ??
  windows.find((w) => String(w.class ?? '').includes('Chrome_WidgetWin') && w.width < 1000)

if (!target) {
  results.push('  ✗ 没找到 Chromium 系窗口')
  console.log(results.join('\n'))
  process.exit(1)
}
// 按靶子的 exe 建一个能操作的实例：空配置会撞 WhitelistEmpty，
// 而那消息只会让人以为"桥不工作"。
const set = createDesktopToolset({ allowedApps: path.basename(String(target.exe ?? '')) })
results.push(`  目标: hwnd=${target.hwnd} class=${target.class} ${target.width}x${target.height}（白名单 ${path.basename(String(target.exe ?? ''))}）`)
results.push('')

results.push('── 1. readWindow 必须返回内容 ──')
{
  const r = await set.tools.readWindow({ hwnd: target.hwnd, maxNodes: 100 })
  const text = r.text ?? ''
  if (r.isError) {
    bad(`readWindow 报错：${text.split('\n')[0]}`)
  } else {
    // 抽 total / named / actionable
    const m = text.match(/(\d+)\s*个节点/)
    const total = m ? Number(m[1]) : -1
    results.push(`    （返回里报告 ${total} 个节点）`)

    if (text.includes('Document') || text.includes('Text') || text.includes('Button')) {
      ok('返回里含真实控件类型（Document/Text/Button 之一）')
    } else {
      bad(`返回里没看到真实控件类型。前 300 字：${text.slice(0, 300)}`)
    }

    if (text.includes('桥')) {
      ok('返回里说明了这棵树来自桥')
    } else {
      results.push('    · 没提到"桥"（可能是 dsc.exe 直读成功了）')
    }

    // 有矩形
    if (/@\(\d+,\d+ \d+x\d+\)/.test(text)) {
      ok('节点带矩形坐标')
    } else {
      bad('节点没有矩形坐标')
    }

    const usable = text.includes('可交互元素') ? '有可用性提示' : ''
    if (usable) results.push(`    · ${usable}`)
  }
}

results.push('')
results.push('── 2. 对照：dsc.exe 直读同一窗口读到几个 ──')
{
  const r = await set.tools.readWindow({ hwnd: target.hwnd, maxNodes: 100 })
  const text = r.text ?? ''
  const m = text.match(/(\d+)\s*个节点/)
  if (m) {
    const n = Number(m[1])
    if (n >= 10) ok(`直读+桥的最终结果有 ${n} 个节点`)
    else bad(`节点数太少：${n}`)
  }
}

console.log(results.join('\n'))
console.log('')
console.log('══════════════════════════════')
if (fail === 0) console.log(`  ${pass} 项通过`)
else console.log(`  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
