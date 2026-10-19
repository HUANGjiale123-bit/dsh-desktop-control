#!/usr/bin/env node
/* 视觉候选：坐标变换、标签归一化、存储级联失效，以及"真实截图 → OCR → 候选 → 窗口像素"这条链。
 *
 * 为什么这些断言值得写：
 *   · 坐标变换错一次，表现是"点偏了一点"，而工具照样报成功 —— 最难发现的那类错；
 *   · 标签归一化不做，中文 OCR 的"我 的"（字间带空格）永远匹配不上；
 *   · 级联失效漏一处，就会出现"点击一个已经不存在的观察"。
 *
 * 这一层不点鼠标、不读屏幕（除了测试自己截一张图喂给 OCR），所以可以放心跑。
 *
 * 用法：node tools/test-visual-candidates.mjs
 */

import { spawnSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs'
import { join } from 'node:path'

import {
  pngSize, cropFromRegion, viewPointToWindow, normalizeLabel, labelMatches,
  candidatesFromOcr, candidatesFromProposal, createCandidateStore, candidatePointToWindow,
} from '../server/visual-candidates.mjs'
import { probeOcr, runOcr, linesToRows } from '../server/ocr-worker.mjs'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`); skipped++ }

/* ══ 1. 坐标变换 ══ */
console.log()
console.log('── 1. 归一化点 → 窗口像素 ──')
{
  // 评审给的例子：原图 2000x1000，从 (1000,200) 裁 800x400，模型指向裁图 (0.25,0.5)
  const snapshot = { image: { width: 2000, height: 1000 }, capture: { x: 0, y: 0, w: 2000, h: 1000 } }
  const view = { crop: { x: 1000, y: 200, w: 800, h: 400 } }
  const got = viewPointToWindow(snapshot, view, { u: 0.25, v: 0.5 })
  if (got.x === 1200 && got.y === 400) ok('评审例子对得上：(0.25,0.5) → (1200,400)')
  else bad(`评审例子算错：期望 (1200,400)，得到 (${got.x},${got.y})`)

  if (got.space === 'window') ok('返回的是 window 空间（原点由 toolset 加，这里不许加）')
  else bad(`space 应为 window，实际 ${got.space}`)

  // 同一张裁图被显示成 800x400 或 400x200，都必须落在同一点
  const scaled = { crop: { x: 1000, y: 200, w: 800, h: 400 }, delivered: { width: 400, height: 200 } }
  const got2 = viewPointToWindow(snapshot, scaled, { u: 0.25, v: 0.5 })
  if (got2.x === got.x && got2.y === got.y) ok('裁图被缩小显示不影响结果（用的是原图坐标，不是显示尺寸）')
  else bad(`裁图缩放影响了结果：${JSON.stringify(got2)} vs ${JSON.stringify(got)}`)

  // 窗口与图片尺寸不一致时（截图被缩放），两轴独立换算
  const odd = { image: { width: 1000, height: 500 }, capture: { x: 0, y: 0, w: 200, h: 300 } }
  const full = { crop: { x: 0, y: 0, w: 1000, h: 500 } }
  const p = viewPointToWindow(odd, full, { u: 0.5, v: 0.5 })
  if (p.x === 100 && p.y === 150) ok('两轴独立换算（1000→200 与 500→300 不同比例）')
  else bad(`两轴换算错：期望 (100,150)，得到 (${p.x},${p.y})`)

  // 越界必须抛，不能 clamp
  for (const [u, v, label] of [[1, 0.5, 'u=1（半开区间外）'], [-0.01, 0.5, 'u<0'], [NaN, 0.5, 'NaN'], [0.5, 1, 'v=1']]) {
    let threw = false
    try { viewPointToWindow(snapshot, view, { u, v }) } catch { threw = true }
    if (threw) ok(`${label} 被拒绝（不 clamp 到边缘）`)
    else bad(`${label} 没有被拒绝`)
  }
}

/* ══ 1b. 候选点（整图）→ 窗口像素：**不能**经过裁图变换 ══ */
console.log()
console.log('── 1b. 候选点 → 窗口像素（子区域也要对）──')
{
  // 评审给的反例：原图 2000x1000，region 是 (1000,200) 800x400，候选点在整图 (1200,400)。
  // 正确结果是窗口 (1200,400)。旧实现把它当"裁图内归一化坐标"又乘了一次 crop，
  // 实际送到点击链的是 (1480,360) —— 点歪了，而且看不出错。
  const image = { width: 2000, height: 1000 }
  const capture = { x: 0, y: 0, w: 2000, h: 1000 }
  const cand = { pointInSourceImage: { x: 1200, y: 400 } }
  const got = candidatePointToWindow(cand, image, capture)
  if (got.x === 1200 && got.y === 400) ok('整图点 (1200,400) → 窗口 (1200,400)（不受 region 影响）')
  else bad(`候选点被二次映射了：期望 (1200,400)，得到 (${got.x},${got.y})`)

  // 截图与窗口尺寸不同时，两轴各自缩放
  const scaled = candidatePointToWindow(cand, image, { x: 0, y: 0, w: 1000, h: 500 })
  if (scaled.x === 600 && scaled.y === 200) ok('截图 2000x1000 / 窗口 1000x500 → (600,200)')
  else bad(`缩放算错：${JSON.stringify(scaled)}`)

  // 越界必须抛，不能夹边
  let threw = false
  try { candidatePointToWindow({ pointInSourceImage: { x: 2500, y: 10 } }, image, capture) } catch { threw = true }
  if (threw) ok('越界点被拒绝（不夹到边界）')
  else bad('越界点没有拒绝')
}

/* ══ 2. 裁剪区域 ══ */
console.log()
console.log('── 2. 归一化区域 → 像素裁剪 ──')
{
  const img = { width: 2000, height: 1000 }
  const c = cropFromRegion(img, { x: 0.5, y: 0, w: 0.5, h: 0.5 })
  if (c.x === 1000 && c.y === 0 && c.w === 1000 && c.h === 500) ok('(0.5,0,0.5,0.5) → (1000,0 1000x500)')
  else bad(`裁剪算错：${JSON.stringify(c)}`)

  const small = cropFromRegion({ width: 3, height: 3 }, { x: 0.5, y: 0.5, w: 0.5, h: 0.5 })
  if (small.w >= 1 && small.h >= 1 && small.x + small.w <= 3 && small.y + small.h <= 3) {
    ok(`极小图不塌成 0 宽高：${small.w}x${small.h} @(${small.x},${small.y})`)
  } else bad(`极小图裁剪越界：${JSON.stringify(small)}`)

  for (const [region, label] of [[{ x: -0.1, y: 0, w: 0.5, h: 0.5 }, 'x<0'], [{ x: 0, y: 0, w: 0, h: 1 }, 'w=0']]) {
    let threw = false
    try { cropFromRegion(img, region) } catch { threw = true }
    if (threw) ok(`${label} 被拒绝`)
    else bad(`${label} 没有被拒绝`)
  }
}

/* ══ 3. 标签归一化 ══ */
console.log()
console.log('── 3. 标签归一化（中文 OCR 会插空格、数字会变全角）──')
{
  if (normalizeLabel('我 的') === '我的') ok('"我 的" → "我的"（去空白）')
  else bad(`"我 的" 归一化成了 ${JSON.stringify(normalizeLabel('我 的'))}`)

  if (normalizeLabel('3．3．2') === '3.3.2') ok('全角数字点号 → 半角')
  else bad(`全角没转：${normalizeLabel('3．3．2')}`)

  if (labelMatches('剩 余 流 量', '剩余流量')) ok('查询"剩余流量"能匹配 OCR 的"剩 余 流 量"')
  else bad('带空格的 OCR 文本匹配不上查询')

  if (!labelMatches('首 页', '我 的')) ok('不相关标签不误匹配')
  else bad('不相关标签被误匹配')

  if (labelMatches('任意', '')) ok('空查询 = 全都要')
  else bad('空查询应当匹配所有')
}

/* ══ 4. 候选构造与存储 ══ */
console.log()
console.log('── 4. 候选构造与级联失效 ──')
{
  const rows = [
    { label: '保 存', quad: [[10, 20], [60, 20], [60, 40], [10, 40]], score: 0.93 },
    { label: '', quad: [[0, 0], [1, 1]] },            // 空标签丢掉
    { label: '坏框', quad: [[NaN, 0], ['x', 1]] },    // 非法坐标丢掉
  ]
  const cands = candidatesFromOcr(rows, { shotId: 's1', viewId: 'v1', ownerId: 'o1' })
  if (cands.length === 1) ok('只有合法行成为候选（空标签/坏框被丢弃）')
  else bad(`期望 1 个候选，得到 ${cands.length}`)

  const c = cands[0]
  if (c.role === 'unknown' && c.actionability === 'unknown') ok('role/actionability 保持 unknown（OCR 不知道能不能点）')
  else bad(`不该替 OCR 断言可点击性：role=${c.role} actionability=${c.actionability}`)

  if (c.scores.textRecognition === 0.93) ok('分数记在 textRecognition（没有改名成置信度）')
  else bad(`分数记错位置：${JSON.stringify(c.scores)}`)

  if (c.pointInSourceImage.x === 35 && c.pointInSourceImage.y === 30) ok('文字锚点取框中心 (35,30)')
  else bad(`锚点算错：${JSON.stringify(c.pointInSourceImage)}`)

  // 模型提的点：越界丢弃，合法保留
  const props = candidatesFromProposal([
    { label: '右边的齿轮', point: { u: 0.5, v: 0.5 } },
    { label: '越界', point: { u: 1.2, v: 0.5 } },
    { label: '无点' },
  ], { shotId: 's1', viewId: 'v2', ownerId: 'o1', image: { width: 100, height: 50 } })
  if (props.length === 1 && props[0].source === 'model_proposal') ok('模型提点只保留合法的那一个，source 标 model_proposal')
  else bad(`模型提点校验不对：${props.length} 个`)

  // 存储级联：作废一张截图，它的 view 和 candidate 必须一起走
  const store = createCandidateStore()
  store.addView({ viewId: 'v1', shotId: 's1', crop: { x: 0, y: 0, w: 10, h: 10 } })
  store.addCandidates([...cands, ...props])
  const before = store.stats()
  const removed = store.invalidateShot('s1')
  const after = store.stats()
  if (removed === 3 && after.views === 0 && after.candidates === 0) {
    ok(`作废 shot 连带清掉 1 个 view + 2 个候选（统计 ${JSON.stringify(before)} → ${JSON.stringify(after)}）`)
  } else {
    bad(`级联失效不完整：removed=${removed} after=${JSON.stringify(after)}`)
  }

  // 按 owner 失效（换会话 / 撤权）
  const s2 = createCandidateStore()
  s2.addCandidates(candidatesFromOcr(rows, { shotId: 's9', viewId: 'v9', ownerId: 'session-A' }))
  s2.addCandidates(candidatesFromOcr(rows, { shotId: 's9', viewId: 'v9', ownerId: 'session-B' }))
  const n = s2.invalidateOwner('session-A')
  const st = s2.stats()
  if (n === 1 && st.candidates === 1) ok('换会话只清掉自己那个 owner 的候选')
  else bad(`owner 失效不对：清掉 ${n}，剩余 ${JSON.stringify(st)}`)
}

/* ══ 5. 真实截图 → OCR → 候选 → 窗口像素 ══ */
console.log()
console.log('── 5. 真实截图 → OCR → 候选 → 窗口像素（端到端）──')
{
  const probe = await probeOcr()
  if (!probe.ok) {
    skip(`这台机器没有可用的 OCR（${probe.error ?? '无语言包'}），端到端这一段跳过`)
  } else {
    console.log(`    OCR 后端：${probe.backend}，语言：${probe.languages.join(', ')}`)

    // ★ 截图必须落在**工作区内**：helper 跑在 Low 完整性下，写工作区之外的路径
    //   会被沙箱拒掉（OutputNotWritable）。这里是 "%TEMP% 更好" 的反例 ——
    //   出题人（测试）比被测量的进程权限更高时，不能按自己的方便挑目录。
    const dir = mkdtempSync(join(process.cwd(), '.cache', 'visual-ocr-'))
    const tmpPng = join(dir, 'shot.png')
    try {
      // 用 helper 截一张真实窗口：比自己造图更有代表性（抗锯齿、缩放、真实字体）
      const listRaw = spawnSync('helper/bin/dsc.exe', ['list_windows'],
        { encoding: 'utf8', windowsHide: true })
      let wins = []
      try { wins = JSON.parse(String(listRaw.stdout ?? '{}')).windows ?? [] } catch { /* ignore */ }
      const ours = (w) => /^(dsc|dsc-esc|textinputhost)\.exe$/i.test(String(w.exe ?? '').split(/[\\/]/).pop())
      const target = wins.find((w) => w.width > 500 && w.height > 400 && w.exe && !ours(w))

      if (!target) {
        skip('找不到可截图的窗口')
      } else {
        const exe = String(target.exe).split(/[\\/]/).pop()
        const shot = spawnSync('helper/bin/dsc.exe',
          ['screenshot', String(target.hwnd), tmpPng, '--allow', exe],
          { encoding: 'utf8', windowsHide: true })
        let shotOk = false
        try { shotOk = JSON.parse(String(shot.stdout ?? '{}')).ok === true } catch { /* ignore */ }
        if (!shotOk || !existsSync(tmpPng)) {
          skip(`helper 截图失败：${String(shot.stdout ?? '').slice(0, 80)}`)
        } else {
          const size = pngSize(readFileSync(tmpPng).subarray(0, 64))
          const r = await runOcr(tmpPng, { lang: probe.languages[0] })
          if (!r.ok) {
            bad(`OCR 失败：${r.error}`)
          } else {
            if (r.imageVerified) ok(`PNG 头与 OCR 自报尺寸一致：${r.image.width}x${r.image.height}`)
            else bad(`尺寸不一致：PNG ${JSON.stringify(size)} vs OCR ${JSON.stringify(r.image)}`)
            console.log(`    OCR 用时 ${r.elapsedMs}ms，${r.lines.length} 行`)

            const rows = linesToRows(r.lines)
            if (rows.length >= 3) ok(`拿到 ${rows.length} 行文字（含坐标）`)
            else bad(`只拿到 ${rows.length} 行，太少`)

            const inside = rows.every((row) => row.quad.every(([x, y]) =>
              x >= 0 && y >= 0 && x <= r.image.width && y <= r.image.height))
            if (inside) ok('所有文字框都落在图片范围内')
            else bad('有文字框跑出图片范围')

            // 完整链：原图像素 → 归一化 → viewPointToWindow → 窗口像素
            const snapshot = {
              image: { width: r.image.width, height: r.image.height },
              capture: { x: 0, y: 0, w: target.width, h: target.height },
            }
            const view = { crop: { x: 0, y: 0, w: r.image.width, h: r.image.height } }
            const first = rows.find((row) => row.label.trim().length > 0)
            if (!first) {
              skip('OCR 没产出可用的文字行')
            } else {
              const u = (first.quad[0][0] + first.quad[2][0]) / 2 / r.image.width
              const v = (first.quad[0][1] + first.quad[2][1]) / 2 / r.image.height
              const wpx = viewPointToWindow(snapshot, view, { u, v })
              const expectX = Math.floor(u * target.width)
              const expectY = Math.floor(v * target.height)
              if (wpx.x === expectX && wpx.y === expectY) {
                ok(`"${first.label.trim()}" 的文字锚点换算正确：(u,v)=(${u.toFixed(3)},${v.toFixed(3)}) → (${wpx.x},${wpx.y})`)
              } else {
                bad(`换算不一致：得到 (${wpx.x},${wpx.y})，期望 (${expectX},${expectY})`)
              }
              if (wpx.x >= 0 && wpx.x < target.width && wpx.y >= 0 && wpx.y < target.height) {
                ok('结果落在窗口内（可以直接交给 space:\'window\' 的点击分支）')
              } else {
                bad(`结果跑出窗口：${JSON.stringify(wpx)} 窗口 ${target.width}x${target.height}`)
              }
            }
          }
        }
      }
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
