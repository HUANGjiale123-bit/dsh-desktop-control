/**
 * 视觉候选：存储、坐标变换、校验。**纯逻辑，不发输入、不读屏幕、不起进程。**
 *
 * ── 为什么单独一个模块 ──
 *
 * "看不看得见"和"点不点得中"是两件事。候选这一层只负责后者的一半：
 * 把「模型/OCR 在图上看到的某个位置」变成一个**可复核、可追溯**的候选，
 * 并且在点击之前能被重新验证。它不做识别，也不做点击 —— 那两件事分别属于
 * 识别器后端和现有的 helper 链路。
 *
 * ── 三条硬约束（来自外部评审，逐条对应实现）──
 *
 * 1. **坐标只算一次**：模型与 OCR 都只报"图内归一化位置"（u/v，半开区间），
 *    由这里的 `viewPointToWindow` 一次性换算成窗口像素。换算完**不能再乘 DPI，
 *    也不能再加窗口原点** —— 加原点那一步在 toolset.click 的 space:'window' 分支里，
 *    重复加就是两次平移。
 *
 * 2. **证据与结论分开**：OCR 分数只能叫「文字识别分数」，不能改名成"可点击置信度"；
 *    文字框不是按钮框。所以候选里 `role`/`actionability` 一律 "unknown"，
 *    `kind` 只记来源真实知道的东西（"text" 或 "point"）。
 *
 * 3. **ID 不透明**：candidateId 由这里随机生成，只有宿主能分配；
 *    归属校验靠 owner/shot 关联，不靠"ID 难猜"。
 */

import { randomUUID } from 'node:crypto'

/** 候选与视图的容量上限。超出按最旧淘汰 —— 这里只防失控，不当缓存用。 */
const MAX_VIEWS = 64
const MAX_CANDIDATES = 512

/* ───────────── PNG 尺寸（不引入依赖） ───────────── */

/**
 * 从 PNG 字节里读真实像素尺寸。
 *
 * 为什么要读它：外部评审要求"解码 PNG 验证图片尺寸"，而不是相信
 * 截图工具自报的数字。两者不一致时，整条 u/v 换算都建立在假的宽高上。
 * 这里只解析 IHDR（8 字节签名 + 4 长度 + 4 类型 + 宽 4 + 高 4），
 * 不做完整解码 —— 我们只需要宽高，不需要像素。
 */
export function pngSize(buf) {
  if (!buf || buf.length < 24) return null
  const sig = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]
  for (let i = 0; i < 8; i++) if (buf[i] !== sig[i]) return null
  if (buf.toString('ascii', 12, 16) !== 'IHDR') return null
  const width = buf.readUInt32BE(16)
  const height = buf.readUInt32BE(20)
  if (!(width > 0 && height > 0)) return null
  return { width, height }
}

/* ───────────── 归一化区域 → 像素裁剪 ───────────── */

function isUnit(v) {
  return typeof v === 'number' && Number.isFinite(v) && v >= 0 && v <= 1
}

/**
 * 把归一化区域（0..1）换成**原图像素**裁剪矩形。
 *
 * `x`,`y` 是左上角比例，`w`,`h` 是宽高比例。取整规则：
 * 左/上边界向下取整、右/下边界向上取整 —— 宁可多切一个像素，
 * 也不要把用户想看的内容切掉一条边。
 */
export function cropFromRegion(image, region) {
  if (!image || !(image.width > 0) || !(image.height > 0)) throw new Error('BadImageSize')
  const r = region ?? { x: 0, y: 0, w: 1, h: 1 }
  if (!isUnit(r.x) || !isUnit(r.y) || !isUnit(r.w) || !isUnit(r.h)) throw new Error('BadRegion')
  if (!(r.w > 0) || !(r.h > 0)) throw new Error('EmptyRegion')

  const x0 = Math.floor(r.x * image.width)
  const y0 = Math.floor(r.y * image.height)
  const x1 = Math.ceil((r.x + r.w) * image.width)
  const y1 = Math.ceil((r.y + r.h) * image.height)

  const x = Math.max(0, Math.min(image.width - 1, x0))
  const y = Math.max(0, Math.min(image.height - 1, y0))
  const w = Math.max(1, Math.min(image.width - x, x1 - x))
  const h = Math.max(1, Math.min(image.height - y, y1 - y))

  if (!(w > 0) || !(h > 0)) throw new Error('EmptyRegion')
  return { x, y, w, h }
}

/* ───────────── 坐标变换：唯一的一条 ───────────── */

/**
 * 视图内归一化点（u,v）→ **窗口内像素**。
 *
 * 两条来源共用这一个函数：OCR 在裁图上算出的点、以及看图模型报的点。
 * 三条规则写死在这里，别处不许再换算：
 *   · u/v 半开区间 [0,1)，越界抛 OutOfRange（**绝不 clamp**，那会把明显点偏的点
 *     悄悄挪到边缘，然后点在一个看起来合法、实际无关的位置上）；
 *   · 先乘回裁剪区内像素，再按"裁剪图像素 / 原图像素"的比例映回原图；
 *   · 取整只做一次，两轴各自用真实尺寸（只记一个宽度比例会在高度上偏）。
 *
 * 返回的是 **窗口空间**：调用方接下来交给 toolset.click 的 space:'window' 分支，
 * 由它加窗口原点。这里加原点就是重复平移。
 */
export function viewPointToWindow(snapshot, view, point) {
  const u = point?.u
  const v = point?.v
  if (!Number.isFinite(u) || !Number.isFinite(v) || u < 0 || u >= 1 || v < 0 || v >= 1) {
    throw new Error('OutOfRange')
  }
  const cap = snapshot?.capture
  const img = snapshot?.image
  const crop = view?.crop
  if (!cap || !img || !crop) throw new Error('BadSnapshot')
  if (!(img.width > 0) || !(img.height > 0) || !(crop.w > 0) || !(crop.h > 0)) {
    throw new Error('BadSnapshot')
  }

  const sx = crop.x + u * crop.w
  const sy = crop.y + v * crop.h
  const x = Math.floor((sx * cap.w) / img.width)
  const y = Math.floor((sy * cap.h) / img.height)

  if (x < 0 || x >= cap.w || y < 0 || y >= cap.h) throw new Error('OutOfRange')
  return { x, y, space: 'window' }
}

/* ───────────── 标签归一化（只用于**匹配**，不改写显示文本） ───────────── */

/**
 * 把一个标签归一化成"可以比对的形状"：去掉所有空白、全角转半角、小写。
 *
 * 实测依据：Windows 自带 OCR 会在汉字之间插空格（"我 的"、"剩 余 流 量"），
 * 数字会变全角（"3.3.2" → "3 ． 3 ． 2"）。UIA 的名字里也可能带空格。
 * 所以两边都归一化再比，比"要求逐字节相等"现实得多。
 *
 * 注意：归一化只用于**匹配**。候选里保存的 `label` 仍然是原始文本 ——
 * 模型看到的应该是界面上真实写的东西，不是我们加工过的。
 */
export function normalizeLabel(s) {
  if (s === null || s === undefined) return ''
  let out = String(s)
  // 全角 ASCII（！到 ～）→ 半角；全角空格 → 普通空格
  out = out.replace(/[\uFF01-\uFF5E]/g, (ch) =>
    String.fromCharCode(ch.charCodeAt(0) - 0xFEE0)).replace(/\u3000/g, ' ')
  return out.replace(/\s+/g, '').toLowerCase()
}

/** 标签是否匹配查询词（都做过归一化）。空查询 = 全都要。 */
export function labelMatches(label, query) {
  const q = normalizeLabel(query)
  if (!q) return true
  return normalizeLabel(label).includes(q)
}

/* ───────────── 候选构造 ───────────── */

function quadBounds(quad) {
  let minX = Infinity
  let minY = Infinity
  let maxX = -Infinity
  let maxY = -Infinity
  for (const p of quad) {
    const [x, y] = p
    if (!Number.isFinite(x) || !Number.isFinite(y)) continue
    if (x < minX) minX = x
    if (y < minY) minY = y
    if (x > maxX) maxX = x
    if (y > maxY) maxY = y
  }
  if (!Number.isFinite(minX) || maxX < minX) return null
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY }
}

/**
 * 把 OCR 的行结果（原图像素坐标 + 文本 + 分数）变成候选。
 *
 * 保持诚实的三件事：
 *   · `kind: 'text'` —— 这是文字锚点，不是按钮；
 *   · `role` / `actionability` 恒为 "unknown" —— OCR 不知道那是不是可点的；
 *   · 分数原样放在 `scores.textRecognition`，**不改名成置信度**。
 */
export function candidatesFromOcr(rows, { shotId, viewId, ownerId }) {
  const out = []
  for (const row of rows ?? []) {
    const label = row?.label
    if (typeof label !== 'string' || !label.trim()) continue
    const quad = Array.isArray(row.quad) ? row.quad.filter((p) => Array.isArray(p) && p.length >= 2) : []
    if (quad.length < 2) continue
    const b = quadBounds(quad)
    if (!b) continue
    out.push({
      candidateId: randomUUID(),
      shotId,
      viewId,
      ownerId,
      source: 'ocr',
      kind: 'text',
      label,
      // 原图像素证据：OCR 给的是四点框，原样保留
      quadInSourceImage: quad.map(([x, y]) => [Number(x), Number(y)]),
      boxInSourceImage: b,
      pointInSourceImage: { x: b.x + b.w / 2, y: b.y + b.h / 2 },
      role: 'unknown',
      actionability: 'unknown',
      scores: Number.isFinite(row?.score) ? { textRecognition: Number(row.score) } : {},
    })
  }
  return out
}

/**
 * 校验**模型提出**的点/框（看图模型报的候选）。
 *
 * 只做"能不能落地"的校验，不假装能判断"模型看对了没有"。
 * 模型给的分数一律忽略 —— 它不是点击许可。
 */
export function candidatesFromProposal(raw, { shotId, viewId, ownerId, image, limit = 32 }) {
  const items = Array.isArray(raw) ? raw : []
  const out = []
  for (const it of items) {
    if (out.length >= limit) break
    const label = typeof it?.label === 'string' ? it.label.slice(0, 200) : ''
    const pt = it?.point
    const u = Number(pt?.u)
    const v = Number(pt?.v)
    if (!Number.isFinite(u) || !Number.isFinite(v) || u < 0 || u >= 1 || v < 0 || v >= 1) continue
    const x = Math.floor(u * image.width)
    const y = Math.floor(v * image.height)
    if (!(x >= 0 && x < image.width && y >= 0 && y < image.height)) continue
    out.push({
      candidateId: randomUUID(),
      shotId,
      viewId,
      ownerId,
      source: 'model_proposal',
      kind: 'point',
      label,
      boxInSourceImage: null,
      pointInSourceImage: { x, y },
      role: 'unknown',
      actionability: 'unknown',
      scores: {},
    })
  }
  return out
}

/* ───────────── 存储：视图与候选，按 shot / owner 级联失效 ───────────── */

/**
 * 候选存储。
 *
 * ── 为什么要这么多失效入口 ──
 *
 * 老经验：截图作废有**好几个**入口（超限淘汰、动作后作废、TTL、撤权、会话结束），
 * 只改其中一个，剩下的会把悬空引用留在那里 —— 而悬空引用的表现是
 * "点击用一个已经不存在的观察"，比"找不到"危险。
 *
 * 所以这里只有一套：任何一次删除都走 `invalidate*`，而它们都会把
 * 关联的 view 和 candidate 一起带走。
 */
export function createCandidateStore({ maxViews = MAX_VIEWS, maxCandidates = MAX_CANDIDATES } = {}) {
  const views = new Map()
  const candidates = new Map()
  const viewsByShot = new Map()
  const candidatesByShot = new Map()

  function index(map, key, id) {
    let set = map.get(key)
    if (!set) {
      set = new Set()
      map.set(key, set)
    }
    set.add(id)
  }
  function unindex(map, key, id) {
    const set = map.get(key)
    if (!set) return
    set.delete(id)
    if (set.size === 0) map.delete(key)
  }

  function evictOldest(map, limit) {
    while (map.size > limit) {
      const oldest = map.keys().next().value
      const rec = map.get(oldest)
      map.delete(oldest)
      if (rec?.shotId) {
        unindex(viewsByShot, rec.shotId, oldest)
        unindex(candidatesByShot, rec.shotId, oldest)
      }
    }
  }

  return {
    addView(view) {
      views.set(view.viewId, view)
      index(viewsByShot, view.shotId, view.viewId)
      evictOldest(views, maxViews)
      return view
    },
    getView(viewId) {
      return views.get(viewId) ?? null
    },
    addCandidates(list) {
      const accepted = []
      for (const c of list ?? []) {
        candidates.set(c.candidateId, c)
        index(candidatesByShot, c.shotId, c.candidateId)
        accepted.push(c)
      }
      evictOldest(candidates, maxCandidates)
      return accepted
    },
    getCandidate(candidateId) {
      return candidates.get(candidateId) ?? null
    },
    listCandidates(shotId) {
      const ids = candidatesByShot.get(shotId)
      if (!ids) return []
      return [...ids].map((id) => candidates.get(id)).filter(Boolean)
    },
    /** 一张截图废掉时，它的视图与候选必须一起走。 */
    invalidateShot(shotId) {
      let n = 0
      for (const id of [...(viewsByShot.get(shotId) ?? [])]) {
        views.delete(id)
        n++
      }
      viewsByShot.delete(shotId)
      for (const id of [...(candidatesByShot.get(shotId) ?? [])]) {
        candidates.delete(id)
        n++
      }
      candidatesByShot.delete(shotId)
      return n
    },
    /** 换会话、撤权：把这个 owner 的全部东西带走。 */
    invalidateOwner(ownerId) {
      let n = 0
      for (const [id, v] of [...views]) {
        if (v.ownerId !== ownerId) continue
        views.delete(id)
        unindex(viewsByShot, v.shotId, id)
        n++
      }
      for (const [id, c] of [...candidates]) {
        if (c.ownerId !== ownerId) continue
        candidates.delete(id)
        unindex(candidatesByShot, c.shotId, id)
        n++
      }
      return n
    },
    /** 只留观测：诊断用。 */
    stats() {
      return { views: views.size, candidates: candidates.size, shots: candidatesByShot.size }
    },
  }
}
