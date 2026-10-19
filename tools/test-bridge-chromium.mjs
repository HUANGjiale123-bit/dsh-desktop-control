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
const { tempHelper, integrityOf } = await import('./fixture.mjs')
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

/* ══ 3. 换一个 **Medium** 的 helper：同一个窗口应当由 dsc.exe 直读，不经过桥 ══
 *
 * 这一格是 2026-10-19 之后加的。之前只测了"桥能读"，而那条路径之所以被走到，
 * 是因为测试跑在工作区里 → helper 是 Low 完整性 → Chromium 只给它外壳。
 * 用户环境里 helper 是 Medium，根本不会走桥 —— 也就是说这个文件过去验的是
 * **开发机的残废路径**，而不是用户实际那条。
 *
 * 现在两边都验：Low → 桥；Medium → 直读。哪一个坏了都要能红。
 */
results.push('')
results.push('── 3. Medium 的 helper 应当直读（不走桥）──')
{
  const helperPath = tempHelper()
  const level = integrityOf(helperPath)
  results.push(`    helper 副本：${helperPath}（integrity=${level}）`)

  if (level === 'Low') {
    bad('临时副本仍然是 Low —— 这台机器的临时目录也被打了标签，这一格没法验')
  } else {
    const direct = createDesktopToolset({
      helperPath,
      allowedApps: path.basename(String(target.exe ?? '')),
    })
    const r = await direct.tools.readWindow({ hwnd: target.hwnd, maxNodes: 100 })
    const text = r.text ?? ''
    // 注意：直读的文本里写的是"共 N 个元素"，而桥的说明里是"拿到 N 个节点"。
    // 只认"个节点"会拿到 -1（第一版就踩了），两种都要认。
    const m = text.match(/共 (\d+) 个元素/) ?? text.match(/(\d+)\s*个节点/)
    const n = m ? Number(m[1]) : -1

    if (r.isError) {
      bad(`Medium helper 读树报错：${text.split('\n')[0]}`)
    } else if (n >= 20) {
      ok(`Medium helper 直读到 ${n} 个节点（用户实际走的就是这条）`)
    } else {
      bad(`Medium helper 只读到 ${n} 个节点 —— 直读路径退化了`)
    }

    if (/这棵树来自 UIA 桥/.test(text)) {
      bad('Medium helper 仍然走了桥 —— 说明直读被判"不可用"，需要查为什么')
    } else {
      ok('没有走桥（说明是 dsc.exe 自己读的）')
    }
  }
}

console.log(results.join('\n'))
console.log('')
console.log('══════════════════════════════')
if (fail === 0) console.log(`  ${pass} 项通过`)
else console.log(`  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
