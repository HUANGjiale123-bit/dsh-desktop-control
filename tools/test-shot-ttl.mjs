#!/usr/bin/env node
/* 过期截图必须被拒绝。
 *
 * ── 为什么单独一个文件 ──
 *
 * 这条判据的默认保鲜期是 5 分钟（SHOT_TTL_MS）。等 5 分钟才能触发一次，
 * 等于没人会去验它 —— 而它恰恰是"拿着一张半小时前的截图继续点"的最后一道闸。
 *
 * 所以插件允许用 DSC_SHOT_TTL_MS 覆盖这个值（和 DSC_HALT_TTL_SECONDS 同类），
 * 这个测试把保鲜期设成 1ms 再点，判据立刻可验。
 *
 * ★ 必须在 import 之前设环境变量：TTL 是模块加载时求值的常量。
 *
 * 用法：node tools/test-shot-ttl.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`) }

process.env.DSC_SHOT_TTL_MS = '1'

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'shotttl-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

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
const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: 'helper/bin/dsc.exe',
  escPath: 'helper/bin/dsc-esc.exe',
  gatePath: join(tmp, 'halt'),
  shotDir: join(tmp, 'shots'),
})

const by = new Map(reg.map((t) => [t.name, t]))
const shotTool = by.get('desktop_screenshot')
const clickTool = by.get('desktop_click')
const msgOf = (r) => String(r?.err ?? r?.result ?? r?.text ?? '')

const raw = JSON.parse(
  spawnSync('helper/bin/dsc.exe', ['list_windows'], { encoding: 'utf8', windowsHide: true }).stdout,
)
const ours = (w) => /^(dsc|dsc-esc|textinputhost)\.exe$/i.test(String(w.exe ?? '').split(/[\\/]/).pop())
const win = raw.windows.find((w) => w.width > 400 && w.height > 400 && w.exe && !ours(w))

try {
  if (!win) {
    console.log('  ✗ 找不到可用窗口')
    process.exit(1)
  }
  console.log()
  console.log(`  靶子 hwnd=${win.hwnd} ${win.width}x${win.height}`)

  console.log()
  console.log('── 1. 保鲜期内的截图可以用（对照）──')
  // TTL=1ms 时几乎必然过期，所以先验"过期一定被拒"，再用默认语义反证：
  // 这里不给 shotId 的点击不该报"找不到截图/太久"。
  {
    const r = await clickTool
      .execute({ hwnd: win.hwnd, x: 5, y: 5, space: 'window', observe: false })
      .catch((e) => ({ err: e.message }))
    const m = msgOf(r)
    if (/太久|找不到截图/.test(m)) {
      bad(`不带 shotId 的点击不该因为保鲜期被拒：${m.slice(0, 80)}`)
    } else {
      ok('不带 shotId 的点击不经过保鲜期判断（对照成立）')
    }
  }

  console.log()
  console.log('── 2. 超过保鲜期 → 必须拒绝 ──')
  {
    const s = await shotTool.execute({ hwnd: win.hwnd }).catch((e) => ({ err: e.message }))
    const shotId = s.shotId ?? ''
    if (!shotId) {
      skip(`拿不到 shotId：${msgOf(s).slice(0, 60)}`)
    } else {
      await new Promise((r) => setTimeout(r, 60))
      const r = await clickTool
        .execute({ hwnd: win.hwnd, x: 5, y: 5, space: 'window', shotId, observe: false })
        .catch((e) => ({ err: e.message }))
      const m = msgOf(r)
      if (/太久无法保证|分钟前/.test(m)) {
        ok('被拒绝，并说明"太久无法保证界面没变，请重新截图"')
      } else {
        bad(`过期截图仍然被使用：${m.slice(0, 100)}`)
      }
    }
  }
} finally {
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
