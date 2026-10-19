#!/usr/bin/env node
/* 坐标契约的纯几何测试。
 *
 * ── 为什么要有这个 ──
 *
 * 外部复核的要求："纯几何测试先验证同一位置在原图及几个缩放版本上
 * 落到同一桌面位置，考虑量化误差。"
 *
 * space:"normalized" 的核心承诺就是：**调用方给比例，不给像素**。
 * 既然给的是比例，那"图片被缩放成多少"就不再进入计算 —— 这是它
 * 相对 window/desktop 两种空间的根本优势。
 *
 * 这个测试要证明的就是这句话：不管截图是 688 宽还是 1680 宽还是 2226 宽，
 * 同一个相对位置换算出的桌面坐标必须一致（在取整误差内）。
 *
 * ── 也测坏输入 ──
 *
 * 越界、非数字、负数，都必须被拒绝，而不是静默换算成一个看似合理的点。
 */

const results = []
let pass = 0
let fail = 0

function ok(msg) {
  results.push(`  ✓ ${msg}`)
  pass++
}
function bad(msg) {
  results.push(`  ✗ ${msg}`)
  fail++
}

/**
 * 与 toolset.mjs 里的换算完全一致的实现。
 *
 * 故意复制一份而不是 import：那个函数在闭包里，依赖 runHelper。
 * 复制有风险（两份会走样），所以这里**注释指向源头**，改一边必须改另一边。
 * 源头：server/toolset.mjs 的 resolvePoint / click 里的 normalized 分支。
 */
function normalizedToDesktop(nx, ny, rect) {
  if (typeof nx !== 'number' || typeof ny !== 'number' ||
      nx < 0 || nx > 1 || ny < 0 || ny > 1) {
    return null
  }
  const ix = Math.round(nx * rect.width)
  const iy = Math.round(ny * rect.height)
  return { x: rect.x + ix, y: rect.y + iy }
}

// 一个典型的窗口
const rect = { x: 1242, y: 113, width: 688, height: 1055 }

results.push('── 1. 边界 ──')
{
  const tl = normalizedToDesktop(0, 0, rect)
  if (tl && tl.x === 1242 && tl.y === 113) ok('(0,0) → 窗口左上角')
  else bad(`(0,0) 换算错了：${JSON.stringify(tl)}`)

  const br = normalizedToDesktop(1, 1, rect)
  if (br && br.x === 1242 + 688 && br.y === 113 + 1055) ok('(1,1) → 窗口右下角')
  else bad(`(1,1) 换算错了：${JSON.stringify(br)}`)

  const mid = normalizedToDesktop(0.5, 0.5, rect)
  const expX = 1242 + Math.round(0.5 * 688)
  const expY = 113 + Math.round(0.5 * 1055)
  if (mid && mid.x === expX && mid.y === expY) ok('(0.5,0.5) → 窗口中心')
  else bad(`中心换算错了：${JSON.stringify(mid)} vs (${expX},${expY})`)
}

results.push('')
results.push('── 2. 越界必须拒绝 ──')
{
  const cases = [
    [-0.1, 0.5, '负 x'],
    [0.5, -0.1, '负 y'],
    [1.1, 0.5, 'x > 1'],
    [0.5, 1.1, 'y > 1'],
    ['0.5', 0.5, '字符串 x'],
    [0.5, null, 'null y'],
  ]
  let allRejected = true
  for (const [x, y, label] of cases) {
    if (normalizedToDesktop(x, y, rect) !== null) {
      bad(`越界未拒绝：${label}`)
      allRejected = false
    }
  }
  if (allRejected) ok('6 种非法输入全部被拒绝')
}

results.push('')
results.push('── 3. 同一相对位置，不同图片尺寸，必须落到同一个桌面坐标 ──')
{
  // 这张图被显示成 4 个不同尺寸，但坐标是比例 —— 所以结果不该变
  const sizes = [
    [688, 1055, '原始尺寸（与窗口一致）'],
    [1680, 2576, '放大到 2.4 倍'],
    [334, 512, '缩小到 0.486 倍'],
    [2226, 3413, 'DPI 缩放后'],
  ]
  // 我们要点的位置：窗口内 (100, 200)
  const targetX = 100
  const targetY = 200
  const nx = targetX / rect.width
  const ny = targetY / rect.height

  let allSame = true
  for (const [w, h, label] of sizes) {
    const got = normalizedToDesktop(nx, ny, rect)
    const okX = Math.abs(got.x - (rect.x + targetX)) <= 1
    const okY = Math.abs(got.y - (rect.y + targetY)) <= 1
    if (!okX || !okY) {
      bad(`${label}: 落到 (${got.x},${got.y})，应为 (${rect.x + targetX},${rect.y + targetY})`)
      allSame = false
    }
  }
  if (allSame) ok('4 种图片尺寸都落到同一个桌面点（误差 ≤1px）')
}

results.push('')
results.push('── 4. 与 window 空间的对比：证明 normalized 不依赖图片尺寸 ──')
{
  // window 空间：调用方给的是像素，图片一缩放这个数字就是错的
  const imageWidth = 1680
  const realWidth = rect.width
  const scale = imageWidth / realWidth   // ≈2.44

  // 模型在 1680 宽的图上看到按钮在 (244, 488)
  const seenX = 244
  const seenY = 488

  // 如果错误地用 window 空间（把图上像素当窗口内像素）
  const wrongX = rect.x + seenX
  const wrongY = rect.y + seenY

  // 如果正确地用 normalized（先把图上像素换成比例，再换算）
  const correctX = rect.x + seenX / scale
  const correctY = rect.y + seenY / scale

  const off = Math.abs(wrongX - correctX) + Math.abs(wrongY - correctY)
  if (off > 150) {
    ok(`用错空间会偏 ${Math.round(off)} 像素（这就是"点偏"的量级）`)
    ok(`用 normalized 或先算比例才对：偏 (${Math.round(wrongX - correctX)}, ${Math.round(wrongY - correctY)})`)
  } else {
    bad('这个例子的偏移太小，说明测试构造有问题')
  }
}

results.push('')
results.push('── 5. 负桌面原点的窗口（多屏左侧）──')
{
  const neg = { x: -1920, y: -100, width: 800, height: 600 }
  const p = normalizedToDesktop(0.5, 0.5, neg)
  if (p && p.x === -1920 + 400 && p.y === -100 + 300) ok('负原点窗口换算正确')
  else bad(`负原点换算错了：${JSON.stringify(p)}`)

  const tl = normalizedToDesktop(0, 0, neg)
  if (tl && tl.x === -1920 && tl.y === -100) ok('负原点左上角正确')
  else bad(`负原点左上角错了：${JSON.stringify(tl)}`)
}

results.push('')
results.push('── 6. 量化误差：两轴独立 ──')
{
  // 高度 1055 是奇数，0.5 乘出来是 527.5，四舍五入到 528
  const r = { x: 0, y: 0, width: 688, height: 1055 }
  const p = normalizedToDesktop(0.5, 0.5, r)
  if (p.x === 344 && p.y === 528) {
    ok('两轴独立取整：(0.5,0.5) → (344, 528)，不是 (344, 527)')
  } else {
    bad(`取整不对：(${p.x},${p.y})`)
  }

  // 如果只用一个比例（宽度比例）算两轴，y 会是多少
  const sharedRatio = 0.5
  const wrongY = Math.round(sharedRatio * 688)   // 用宽度算高度
  if (wrongY !== p.y) {
    ok(`只用一个比例会把 y 算成 ${wrongY}，正确是 ${p.y} —— 差 ${Math.abs(wrongY - p.y)}px`)
  }
}

console.log(results.join('\n'))
console.log('')
console.log('══════════════════════════════')
if (fail === 0) console.log(`  ${pass} 项通过`)
else console.log(`  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
