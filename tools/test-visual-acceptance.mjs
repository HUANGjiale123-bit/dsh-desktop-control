#!/usr/bin/env node
/* 视觉兜底验收第 4、5、6 条：**该拒绝的时候必须拒绝**。
 *
 *   5. 推理/取候选之后、真正点击之前按了 ESC → 输入必须被闸门拦下；
 *   4. OCR 期间观察被作废/淘汰 → 迟到的识别结果**不能登记**（源码级断言 +
 *      说明为什么没法在集成层面稳定复现）；
 *   6. 点击已发出但事后观察失败 → **只报告不确定，不重试**（退回"点了没"而不是"再点一次"）。
 *
 * 用法：node tools/test-visual-acceptance.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync, existsSync } from 'node:fs'
import { spawn } from 'node:child_process'
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
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-acc-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
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

const shotDir = join(tmp, 'shots')
const gatePath = join(tmp, 'halt')
const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: HELPER,
  escPath: 'helper/bin/dsc-esc.exe',
  shotDir,
  gatePath,
})

const by = new Map(reg.map((t) => [t.name, t]))
const shotTool = by.get('desktop_screenshot')
const candTool = by.get('desktop_visual_candidates')
const clickCand = by.get('desktop_click_candidate')

function parseCandidates(text) {
  const out = []
  const re = /#\d+ ([0-9a-f-]{36}) {2}"([^"]*)" {2}@\((\d+),(\d+) (\d+)x(\d+)\)/g
  for (const m of String(text).matchAll(re)) {
    out.push({ id: m[1], label: m[2] })
  }
  return out
}

/** 读光标位置（helper 的 cursor 命令，只读）。用来证明"真的没有输入"。 */
function cursorPos(helper) {
  try {
    const r = spawnSync(helper, ['cursor'], { encoding: 'utf8', windowsHide: true, timeout: 15000 })
    const j = JSON.parse(r.stdout)
    return j.ok ? { x: j.x, y: j.y } : null
  } catch { return null }
}

const wins = (() => {
  try {
    return JSON.parse(spawnSync(HELPER, ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
  } catch { return [] }
})()
const win = wins.find((w) => String(w.class ?? '').includes('Tauri'))

try {
  console.log()
  if (!win) {
    console.log('  ✗ 没找到 SuYou 窗口')
    process.exit(1)
  }
  console.log(`  靶子 hwnd=${win.hwnd} ${win.width}x${win.height}`)

  /* ── 5. 取完候选、点击之前按 ESC ── */
  console.log()
  console.log('── 5. 取完候选之后按 ESC，点击必须被拦下 ──')
  {
    const s = await shotTool.execute({ hwnd: win.hwnd })
    const shotId = s.shotId
    const l = await candTool.execute({ hwnd: win.hwnd, shotId })
    const list = parseCandidates(l.result ?? '')
    const target = list.find((c) => /当前版本|SuYou|规则模式/.test(c.label.replace(/\s+/g, '')))
      ?? list[0]

    if (!target) {
      skip('没取到候选，这一格没验')
    } else {
      writeFileSync(gatePath, 'test-halt')
      const cursorBefore = cursorPos(HELPER)
      try {
        const r = await clickCand.execute({ hwnd: win.hwnd, shotId, candidateId: target.id, observe: false })
          .catch((e) => ({ err: e.message }))
        const t = r.err ?? r.result ?? r.text ?? ''
        if (/ESC 叫停|Halted/.test(t)) ok('被闸门拦下，报告写明是"用户叫停"而不是技术失败')
        else bad(`闸门拉着却仍然执行了：${t.slice(0, 200)}`)

        /* "没有发送输入"要有**证据**，不能只看报告文字（评审指出：那证明的是文字）。
         * 真点击一定会把光标移到目标上，所以读两次光标位置对比。 */
        const cursorAfter = cursorPos(HELPER)
        if (cursorBefore && cursorAfter &&
            cursorBefore.x === cursorAfter.x && cursorBefore.y === cursorAfter.y) {
          ok(`光标没有移动（${cursorBefore.x},${cursorBefore.y}）—— 真的没发输入`)
        } else {
          bad(`光标动了：${JSON.stringify(cursorBefore)} → ${JSON.stringify(cursorAfter)}`)
        }
        if (/已点击候选/.test(t)) bad('闸门拉着时居然点了下去')
      } finally {
        rmSync(gatePath, { force: true })
      }

      // 闸门清掉之后应当恢复可用（证明拦的是闸门，不是别的东西坏了）
      const after = await clickCand.execute({
        hwnd: win.hwnd, shotId, candidateId: target.id, observe: false,
      }).catch((e) => ({ err: e.message }))
      const t2 = after.err ?? after.result ?? after.text ?? ''
      if (/已点击候选/.test(t2)) ok('闸门清掉后立刻恢复可用')
      else skip(`闸门清掉后仍然失败（可能是窗口被移动/候选已失效）：${t2.slice(0, 120)}`)
    }
  }

  /* ── 5b. 急停也要盖住"读旧图"这条路 ──
   *
   * 取候选不经过 helper，所以底层闸门管不到它 —— 之前按了 ESC 照样 OCR、
   * 照样把界面文字返回来。这里验的是它自己查闸门。
   */
  console.log()
  console.log('── 5b. 闸门拉着时，连旧图都不许读 ──')
  {
    const s = await shotTool.execute({ hwnd: win.hwnd })
    writeFileSync(gatePath, 'test-halt')
    try {
      const r = await candTool.execute({ hwnd: win.hwnd, shotId: s.shotId })
        .catch((e) => ({ err: e.message }))
      const t = r.err ?? r.result ?? r.text ?? ''
      if (/Halted/.test(t) && /没有读任何界面内容/.test(t)) {
        ok('visual_candidates 在急停时拒绝，并说明没有读界面内容')
      } else {
        bad(`急停时仍然读了界面：${t.slice(0, 200)}`)
      }
      // 有任何界面文字漏出来都算失败
      if (/候选|OCR/.test(t.replace(/没有读任何界面内容/g, ''))) {
        bad('急停的返回里仍然夹带了界面内容')
      } else {
        ok('返回里没有夹带任何界面文字')
      }
    } finally {
      rmSync(gatePath, { force: true })
    }
  }

  /* ── 4. OCR 期间观察失效 → 不登记 ──
   *
   * 这一条**没法在集成层面稳定复现**：OCR 只要 100~300ms，而"作废观察"的
   * 动作（另一个工具调用、淘汰 24 条、撤权）都比它慢且依赖时序。
   * 与其写一个靠 sleep 和竞态的假测试，这里做源码级断言 ——
   * 明确承认它挡的是"实现被改回去"，不是"行为已验证"。
   */
  console.log()
  console.log('── 4. OCR 期间观察失效 → 不登记候选（源码级）──')
  {
    const src = readFileSync(join('plugin', 'index.mjs'), 'utf8')
    const ocrAt = src.indexOf('runOcr(rec.filepath')
    const genAt = src.indexOf('stillThere', ocrAt)
    const regAt = src.indexOf('candidatesFromOcr', ocrAt)
    if (ocrAt > 0 && genAt > ocrAt && regAt > genAt) {
      ok('OCR 之后、登记候选之前，先复检记录与 generation')
    } else {
      bad('没有在 OCR 之后复检观察是否仍然有效 —— 迟到的结果会被登记')
    }

    if (/Number\(stillThere\.generation\) !== Number\(rec\.generation\)/.test(src)) {
      ok('判据是 generation（记录被替换/重建也能发现）')
    } else {
      bad('generation 判据被改动或缺失')
    }
    skip('行为层面（真让 OCR 跨过作废点）没有稳定复现手段，属实现约束而非实测')
  }

  /* ── 6. 点击已发出、但事后观察失败 → 只报不确定，不重试 ──
   *
   * 造一个"被点之后就自己关掉"的窗口：点击确实发出去了，但等我们要看结果时
   * 窗口已经没了 —— 这正是"结果未知"的真实形状。
   *
   * 一开始试过"把截图目录换成文件让观察失败"，那是错的：候选的 PNG 也在那个
   * 目录里，于是失败发生在**读取历史截图**这一步，报的是"文件不在了"，
   * 根本走不到点击。
   */
  console.log()
  console.log('── 6. 点了但观察失败：不许重试 ──')
  {
    const PS = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
    // 夹具**自己**把每次点击写进这个文件 —— 数它，而不是数我们报告里的字符串。
    const clickLog = join(tmp, 'clicks.log')
    const fixture = spawn(PS, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-File', 'tools/fixtures/closing-window.ps1',
      '-LogPath', clickLog], { stdio: ['ignore', 'pipe', 'pipe'] })
    let win2 = null
    for (let i = 0; i < 20 && !win2; i++) {
      await new Promise((r) => setTimeout(r, 250))
      try {
        const ws = JSON.parse(spawnSync(HELPER, ['list_windows'],
          { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
        win2 = ws.find((w) => String(w.title ?? '').includes('dsc-closing-fixture'))
      } catch { /* 下一轮再试 */ }
    }

    try {
      if (!win2) {
        skip('造不出"点了就关"的窗口，这一格没验')
      } else {
        /* 窗口刚出现时可能在重绘，旧图会"对不上"（StaleVisualTarget）。
         * 那不是这一格要验的东西 —— 这一格要验的是"点了但看不到结果"。
         * 所以拿一张新图重试，最多三次；三次都因为重绘被拒才报问题。
         */
        let t = ''
        let hits = 0
        let attempts = 0
        for (; attempts < 3; attempts++) {
          const s = await shotTool.execute({ hwnd: win2.hwnd })
          if (!s?.shotId) break
          const l = await candTool.execute({ hwnd: win2.hwnd, shotId: s.shotId })
          const target = parseCandidates(l.result ?? '')
            .find((c) => /BUTTON/i.test(c.label.replace(/\s+/g, '')))
          if (!target) break
          const r = await clickCand.execute({
            hwnd: win2.hwnd, shotId: s.shotId, candidateId: target.id, observe: true,
          }).catch((e) => ({ err: e.message }))
          t = r.err ?? r.result ?? r.text ?? ''
          if (!/Stale|Unstable/.test(t)) break
          await new Promise((r2) => setTimeout(r2, 400))
        }
        if (/动作已执行/.test(t) && /截图失败/.test(t)) {
          ok('报告分得清"点了"和"没看到结果"（动作已执行 + 自动截图失败）')
        } else if (/已点击候选/.test(t) && !/截图失败/.test(t)) {
          skip(`观察没失败（窗口还活着），这一格没验：${t.slice(0, 120)}`)
        } else {
          bad(`预期之外的返回（试了 ${attempts + 1} 次）：${t.slice(0, 200)}`)
        }
        /* 真点击次数：读夹具自己写的日志。
         * 之前数的是我们报告里出现几次"已点击候选" —— 那只能证明文字写了几遍，
         * 不能证明鼠标按了几次（评审原话）。 */
        const clickLines = existsSync(clickLog)
          ? readFileSync(clickLog, 'utf8').split('\n').filter((l) => l.trim()).length
          : 0
        if (clickLines === 1) ok('夹具记录到 **1 次真实点击**（没有因为看不结果就重试）')
        else if (clickLines === 0) skip('这次没有产生真实点击')
        else bad(`真实点击了 ${clickLines} 次 —— 观察失败后被重试了`)
      }
    } finally {
      try { fixture.kill() } catch { /* 它自己也会退 */ }
    }
  }
} finally {
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
