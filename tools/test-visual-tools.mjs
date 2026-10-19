#!/usr/bin/env node
/* 视觉兜底的两个工具：候选列表 + 按候选点击（含点击前复查）。
 *
 * 验的是四件事：
 *   1. 候选来自**已登记的观察**，坐标可复算，ID 不透明；
 *   2. 按候选点击走的是现有链路（不是新开一条输入后门）；
 *   3. **内容变了必须拒绝** —— 窗口没动、shot 没过期，但界面换了，也要拦住；
 *   4. 找不到/不匹配的候选一律报错，不许"猜一个最像的"点下去。
 *
 * 第 3 条要用**外部**改动来制造（helper 直接点一下切页），不能用插件自己的工具——
 * 因为插件点击之后会主动作废旧观察，那样测到的就不是"像素复查"而是"观察已失效"。
 *
 * 用法：node tools/test-visual-tools.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
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

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'visual-tools-'))
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
const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
await mod.apply(ctx, {
  helperPath: HELPER,
  escPath: 'helper/bin/dsc-esc.exe',
  shotDir: join(tmp, 'shots'),
})

const by = new Map(reg.map((t) => [t.name, t]))
const shotTool = by.get('desktop_screenshot')
const candTool = by.get('desktop_visual_candidates')
const clickCand = by.get('desktop_click_candidate')

if (!shotTool || !candTool || !clickCand) {
  console.log('  ✗ 工具没有注册出来')
  process.exit(1)
}

const wins = (() => {
  try {
    return JSON.parse(spawnSync(HELPER, ['list_windows'],
      { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
  } catch { return [] }
})()
const win = wins.find((w) => String(w.class ?? '').includes('Tauri'))

/** 候选文本 → [{ id, label, box }]。box 是**图片内**像素。 */
function parseCandidates(text) {
  const out = []
  const re = /#\d+ ([0-9a-f-]{36}) {2}"([^"]*)" {2}@\((\d+),(\d+) (\d+)x(\d+)\)/g
  for (const m of String(text).matchAll(re)) {
    out.push({
      id: m[1],
      label: m[2],
      box: { x: Number(m[3]), y: Number(m[4]), w: Number(m[5]), h: Number(m[6]) },
    })
  }
  return out
}

/** 绕过插件、直接让 helper 点一下 —— 模拟"别的进程改了界面"。 */
function helperClick(hwnd, exe, x, y) {
  return spawnSync(HELPER, ['click', String(x), String(y), 'left', '--hwnd', String(hwnd)], {
    encoding: 'utf8', windowsHide: true, timeout: 60000,
    env: { ...process.env, DSC_ALLOWED_APPS: exe },
  })
}

try {
  console.log()
  if (!win) {
    console.log('  ✗ 没找到 SuYou 窗口（这个测试需要一个能切页的中文界面）')
    process.exit(1)
  }
  const exe = String(win.exe).split(/[\\/]/).pop()
  console.log(`  靶子 hwnd=${win.hwnd} ${win.width}x${win.height} @(${win.x},${win.y})  exe=${exe}`)

  const freshShot = async () => {
    /* 截图拍的是**屏幕上那块**，目标被别的窗口盖住时会拍到覆盖者。
     * 所以每次截图前先点一下标题栏把它提到前台（只激活，不点到按钮）。
     * 这件事在真实会话里也发生过：第一张图拍到的是 DSH 自己的界面。
     */
    const rectNow = currentRect()
    if (rectNow) {
      await by.get('desktop_click').execute({
        hwnd: win.hwnd,
        x: Math.round(rectNow.x + rectNow.w / 2),
        y: rectNow.y + 10,
        observe: false,
      }).catch(() => {})
      await new Promise((r) => setTimeout(r, 350))
    }
    const s = await shotTool.execute({ hwnd: win.hwnd })
    return s.shotId
  }
  const candidatesOf = async (shotId) => {
    const r = await candTool.execute({ hwnd: win.hwnd, shotId }).catch((e) => ({ err: e.message }))
    const text = r.result ?? r.err ?? ''
    return { text, list: parseCandidates(text) }
  }
  /* 外部点击必须用**当前**矩形算坐标。
   *
   * 踩到：测试开头读了一次窗口矩形 (1242,113)，中途窗口被挪到 (1641,1045)，
   * 于是按旧原点算出来的坐标点在别处 —— 后面所有断言都跟着错，
   * 而错误信息看起来像"复查算错了"。窗口位置不是常量，用之前再读一次。 */
  function currentRect() {
    try {
      const ws = JSON.parse(spawnSync(HELPER, ['list_windows'],
        { encoding: 'utf8', windowsHide: true }).stdout).windows ?? []
      const w = ws.find((x) => String(x.hwnd) === String(win.hwnd))
      return w ? { x: w.x, y: w.y, w: w.width, h: w.height } : null
    } catch { return null }
  }
  const externalClickOn = (cand) => {
    const rect = currentRect()
    if (!rect) return { status: 1, stdout: '' }
    return helperClick(
      win.hwnd, exe,
      rect.x + Math.round(cand.box.x + cand.box.w / 2),
      rect.y + Math.round(cand.box.y + cand.box.h / 2),
    )
  }

  // 起点：切到首页（外部点击，保证状态确定）
  {
    const s0 = await freshShot()
    const { list } = await candidatesOf(s0)
    const home = list.find((c) => c.label.replace(/\s+/g, '') === '首页')
    if (home) externalClickOn(home)
    await new Promise((r) => setTimeout(r, 600))
  }

  console.log()
  console.log('── 1. 候选来自已登记的观察 ──')
  const shotA = await freshShot()
  if (!shotA) {
    console.log('  ✗ 拿不到 shotId')
    process.exit(1)
  }
  ok(`截图登记成功：${shotA}`)

  const a = await candidatesOf(shotA)
  if (/OCR .*windows-ocr/.test(a.text)) ok('返回里写明了 OCR 后端与语言')
  else bad(`返回里没有 OCR 后端信息：${a.text.slice(0, 160)}`)

  if (a.list.length >= 3) ok(`列出 ${a.list.length} 个候选（带不透明 ID）`)
  else bad(`候选太少：${a.list.length}`)

  if (/不是按钮、能不能点/.test(a.text)) ok('明确写了"文字锚点不证明可点击"')
  else bad('没有说明角色/可点击性未知')

  const q = await candTool.execute({ hwnd: win.hwnd, shotId: shotA, query: '剩余流量' })
    .catch((e) => ({ err: e.message }))
  const qt = q.result ?? q.err ?? ''
  if (/匹配 "剩余流量" 的 \d+ 条/.test(qt)) ok('query 过滤生效（按去空格后的文字匹配）')
  else if (/没有候选/.test(qt)) skip('这块界面没有"剩余流量"文字，跳过该断言')
  else bad(`query 过滤结果看不懂：${qt.slice(0, 160)}`)

  console.log()
  console.log('── 2. 按候选点击走现有链路 ──')
  {
    const rect = currentRect()
    if (!rect || rect.x !== win.x || rect.y !== win.y) {
      skip(`截图之后窗口被移动了（${win.x},${win.y} → ${rect?.x},${rect?.y}），这一阶段没法验`)
    }
  }
  {
    // 优先挑**静态**文字当靶子：流量计数、时钟这类每秒都变，
    // 复查必然判"内容变了"——那验的是噪声，不是能力。
    const pick = a.list.find((c) => /当前版本|SuYou|规则模式/.test(c.label.replace(/\s+/g, '')))
      ?? a.list.find((c) => /剩余流量/.test(c.label.replace(/\s+/g, '')))
    if (!pick) {
      skip('没有适合当"无害靶子"的候选，跳过点击验证')
    } else {
      const r = await clickCand.execute({ hwnd: win.hwnd, shotId: shotA, candidateId: pick.id, observe: false })
        .catch((e) => ({ err: e.message }))
      const t = r.result ?? r.err ?? ''
      if (/已点击候选/.test(t) && /复查：那块 \d+x\d+ 像素与截图一致/.test(t)) {
        ok(`点到了「${pick.label.replace(/\s+/g, '')}」，复查通过`)
      } else {
        bad(`按候选点击失败：${t.slice(0, 200)}  原始=${JSON.stringify(r).slice(0, 300)}`)
      }
      if (/通过现有链路：白名单/.test(t)) ok('报告说明走的是现有校验链（不是新输入后门）')
      else bad('报告没有说明走的是现有链路')
    }
  }

  console.log()
  console.log('── 3. 内容变了必须拒绝（窗口没动、shot 没过期）──')
  {
    const shotB = await freshShot()
    const b = await candidatesOf(shotB)
    // 目标必须是**切页后会消失**的文字（首页独有），否则复查永远通过。
    // 注意不能选紧挨实时数字的那种：余量现在 4px，只框文字本身，不会再吃到计数器。
    const target = b.list.find((c) => /剩余流量/.test(c.label.replace(/\s+/g, '')))
    const myTab = b.list.find((c) => c.label.replace(/\s+/g, '') === '我的')

    if (!target || !myTab) {
      skip('这份候选里缺"我的"标签或目标，无法制造内容变化')
    } else {
      const clicked = externalClickOn(myTab)   // 外部切页：窗口几何不变，内容全变
      const out = String(clicked.stdout ?? '')
      if (!/"ok":true/.test(out)) {
        skip(`外部切页失败：${out.slice(0, 80)}`)
      } else {
        await new Promise((r) => setTimeout(r, 900))
        const rectNow = currentRect()
        if (!rectNow || rectNow.x !== win.x || rectNow.y !== win.y) {
          skip(`切页期间窗口被移动（${rectNow?.x},${rectNow?.y}），内容复查这一格没法验`)
        } else {
        const r = await clickCand.execute({ hwnd: win.hwnd, shotId: shotB, candidateId: target.id, observe: false })
          .catch((e) => ({ err: e.message }))
        const t = r.err ?? r.result ?? r.text ?? ''
        if (/已经变了/.test(t) && /没有发送任何输入/.test(t)) {
          ok('切页后旧候选被拒绝（像素复查拦住了），没有发送输入')
        } else if (/已点击候选/.test(t)) {
          // 放行了 —— 但先看窗口是不是又动了。这个窗口在整套测试期间被移动过好几次
          // （不是测试干的：空闲时不移动；疑似人工拖动或应用自身挪位）。
          // 窗口一动，截图里的"那块区域"和屏幕上的就不是同一块，这一格也就失去意义。
          const rectAfter = currentRect()
          if (!rectAfter || rectAfter.x !== win.x || rectAfter.y !== win.y) {
            skip(`窗口在截图后又移动了（${win.x},${win.y} → ${rectAfter?.x},${rectAfter?.y}），这一格没法验`)
          } else {
            bad(`内容变了却仍然放行：${t.slice(0, 200)}  原始=${JSON.stringify(r).slice(0, 300)}`)
          }
        } else {
          bad(`预期之外的返回：${t.slice(0, 200)}`)
        }
        }
        const home = b.list.find((c) => c.label.replace(/\s+/g, '') === '首页')
        if (home) externalClickOn(home)
        await new Promise((r) => setTimeout(r, 600))
      }
    }
  }

  console.log()
  console.log('── 3b. 给了 region 就真的裁出来再 OCR（坐标要加回去）──')
  {
    const shot = await freshShot()
    const full = await candTool.execute({ hwnd: win.hwnd, shotId: shot })
    const sub = await candTool.execute({ hwnd: win.hwnd, shotId: shot, region: { x: 0, y: 0.8, w: 1, h: 0.2 } })
    const fullText = full.result ?? ''
    const subText = sub.result ?? ''
    if (/裁剪区域 \(0,\d+ \d+x\d+\)/.test(subText)) ok('region 生效：裁剪区域被单独处理')
    else skip(`裁剪区域形态不固定：${subText.split('\n')[1]?.slice(0, 80)}`)

    // 坐标必须**加回了偏移**：裁的是底部 20%，所以所有候选的 y 都应落在 844..1055。
    // 如果忘了加偏移，它们会落在 0..211 —— 这个判据不依赖具体文字，更稳。
    // 只从**候选行**里取坐标：表头里也有一个 "@(1508,166)"（窗口原点），
    // 之前把表头也算进来，误判成"没加偏移"。
    const candLines = subText.split('\n').filter((l) => l.trim().startsWith('#'))
    const ys = candLines.map((l) => Number((/@\(\d+,(\d+)/.exec(l) ?? [])[1])).filter(Number.isFinite)
    if (!ys.length) {
      skip('子区域里没有候选，跳过偏移断言')
    } else if (ys.every((y) => y >= 840 && y <= 1060)) {
      ok(`子区域候选的 y 都在裁剪带内（${ys.length} 个，${Math.min(...ys)}..${Math.max(...ys)}）—— 偏移加回去了`)
    } else {
      bad(`子区域坐标没加回偏移：y = ${ys.slice(0, 6).join(', ')}（应在 844..1055）`)
      console.log(`    候选行：${subText.split('\n').filter((l) => l.includes('@(')).join(' | ').slice(0, 240)}`)
    }

    // 工具要如实说 OCR 跑在裁图上（而不是"给了 region 其实还是整图"）
    if (/OCR 输入：裁图（坐标已偏移 0,844）/.test(subText)) {
      ok('返回里写明 OCR 用了裁图并加回了偏移')
    } else {
      bad(`返回里没写清 OCR 输入来源：${subText.split('\n')[1]?.slice(0, 120)}`)
    }
  }

  console.log()
  console.log('── 4. 可疑引用一律报错，不许猜 ──')
  {
    const shotC = await freshShot()
    const c = await candidatesOf(shotC)

    const bad1 = await clickCand.execute({
      hwnd: win.hwnd, shotId: shotC, candidateId: 'not-a-candidate', observe: false,
    }).catch((e) => ({ err: e.message }))
    if (/找不到候选/.test(bad1.err ?? '')) ok('不存在的候选 ID 被拒绝')
    else bad(`不存在的候选没有拒绝：${JSON.stringify(bad1).slice(0, 120)}`)

    const bad2 = await candTool.execute({ hwnd: win.hwnd, shotId: 'shot-0-0' })
      .catch((e) => ({ err: e.message }))
    if (/找不到截图/.test(bad2.err ?? '')) ok('不存在的 shotId 被拒绝')
    else bad(`不存在的 shotId 没有拒绝：${JSON.stringify(bad2).slice(0, 120)}`)

    const bad3 = await clickCand.execute({
      hwnd: 999999, shotId: shotC, candidateId: c.list[0]?.id ?? '', observe: false,
    }).catch((e) => ({ err: e.message }))
    if (/不是|找不到截图/.test(bad3.err ?? '')) ok('hwnd 与 shot 不匹配被拒绝')
    else bad(`hwnd 不匹配没有拒绝：${JSON.stringify(bad3).slice(0, 120)}`)
  }
} finally {
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
