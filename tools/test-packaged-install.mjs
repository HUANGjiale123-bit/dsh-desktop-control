#!/usr/bin/env node
/* 验收第 8 条：从**实际打包出来的位置**跑一遍完整流程。
 *
 * 为什么单独验：开发目录能跑不代表发出去的包能跑。这个项目出过两次真实事故 ——
 * sync 列表是硬编码的，新增 tooldsl.mjs 后插件直接 ERR_MODULE_NOT_FOUND，
 * 表现是"所有 desktop_* 工具都不见了"。所以验收必须解包、从解出来的目录加载。
 *
 * 流程：
 *   解包 tgz → 核对文件齐全 → 从解包目录 import 插件 → 截图 → 取视觉候选
 *   → 按候选点击 → 读窗口 → 报告 DPI 与落点
 *
 * 边界（写在结论里，不假装）：
 *   · helper 要复制到 %TEMP% 才能点（工作区带 Low 标签，SetCursorPos 会被拒）——
 *     这测的是"打包内容",不是"打包路径的完整性级别"；
 *   · 本机只有一块屏、当前 125%；**100% 缩放、多屏混合 DPI 没法在本机验**，
 *     只能标"未实测"。
 *
 * 用法：node tools/test-packaged-install.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync, existsSync, readdirSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tmpdir } from 'node:os'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`); skipped++ }

/* 最新的 tgz */
const dist = 'dist'
const tgz = (() => {
  try {
    const files = readdirSync(dist).filter((f) => f.endsWith('.tgz'))
    files.sort()
    return files.length ? join(dist, files[files.length - 1]) : null
  } catch { return null }
})()

if (!tgz) {
  console.log('  ✗ dist/ 里没有 .tgz，先跑 pnpm pack')
  process.exit(1)
}
console.log()
console.log(`  安装包: ${tgz}`)

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const work = mkdtempSync(join(process.cwd(), '.cache', 'install-'))
const extractDir = join(work, 'extracted')
mkdirSync(extractDir, { recursive: true })

function run(cmd, args, opts = {}) {
  return spawnSync(cmd, args, { encoding: 'utf8', windowsHide: true, ...opts })
}

try {
  console.log()
  console.log('── 1. 解包 ──')
  const untar = run('tar', ['-xzf', tgz, '-C', extractDir])
  if (untar.status !== 0) {
    console.log(`  ✗ 解包失败：${String(untar.stderr ?? '').slice(0, 200)}`)
    process.exit(1)
  }
  const pkgDir = join(extractDir, 'package')
  if (!existsSync(pkgDir)) {
    console.log('  ✗ 包里没有 package/ 目录')
    process.exit(1)
  }
  ok(`解包到 ${pkgDir}`)

  console.log()
  console.log('── 2. 文件齐全（缺一个就是"工具消失"）──')
  const required = [
    'index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
    'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1',
    'package.json', 'cordis.patch.yml', 'uia-bridge.py',
    'bin/dsc.exe', 'bin/dsc-esc.exe',
  ]
  const missing = required.filter((f) => !existsSync(join(pkgDir, f)))
  if (missing.length === 0) ok(`${required.length} 个必需文件都在`)
  else bad(`缺文件：${missing.join(', ')}`)

  const pkgJson = JSON.parse(readFileSync(join(pkgDir, 'package.json'), 'utf8'))
  const indexSrc = readFileSync(join(pkgDir, 'index.mjs'), 'utf8')
  const m = /PLUGIN_VERSION = '([^']+)'/.exec(indexSrc)
  if (m && m[1] === pkgJson.version) ok(`版本一致：${pkgJson.version}`)
  else bad(`版本不一致：package.json=${pkgJson.version}，index.mjs=${m?.[1]}`)

  console.log()
  console.log('── 3. 从解包目录加载插件 ──')
  // helper 复制到 %TEMP%：工作区带 Low 标签，从那里跑会 SetCursorPos 被拒
  const helperCopy = join(tmpdir(), `dsc-packaged-${Date.now()}.exe`)
  writeFileSync(helperCopy, readFileSync(join(pkgDir, 'bin', 'dsc.exe')))

  const reg = []
  const ctx = {
    logger: { info: () => {}, warn: () => {}, error: () => {} },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    get(n) {
      if (n === 'approval') return { request: async () => 'allowed-once' }
      if (n === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
      if (n === 'workspaceRegistry') return { list: () => [{ path: work, sessionIds: ['s1'] }] }
      if (n === 'attachments') return { saveImages: async () => [{ id: 'a' }] }
      return undefined
    },
  }
  const mod = await import(pathToFileURL(join(pkgDir, 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, { helperPath: helperCopy, escPath: join(pkgDir, 'bin', 'dsc-esc.exe'), shotDir: join(work, 'shots') })
  const by = new Map(reg.map((t) => [t.name, t]))
  if (reg.length >= 20) ok(`注册出 ${reg.length} 个工具`)
  else bad(`只注册出 ${reg.length} 个工具`)

  console.log()
  console.log('── 4. DPI 与屏幕 ──')
  const ping = JSON.parse(run(helperCopy, ['ping'], { env: { ...process.env, DSC_ALLOWED_APPS: '' } }).stdout)
  ok(`DPI 感知：${ping.dpiAwareness ?? '?'}（initOk=${ping.dpiInitOk}）`)
  const wins0 = JSON.parse(run(helperCopy, ['list_windows'], { env: { ...process.env, DSC_ALLOWED_APPS: '' } }).stdout)
  console.log(`    显示器: ${wins0.monitors ?? '?'} 块`)
  if ((wins0.monitors ?? 1) === 1) skip('只有一块屏：多屏混合 DPI 没法在本机验')
  skip('100% 缩放需要改系统 DPI 后重新登录，本机没法验（当前 125%）')

  console.log()
  console.log('── 5. 完整流程（截图 → 候选 → 按候选点击 → 读窗口）──')
  /* 按面积挑，别按"第一个 Tauri"挑：
   * 实测 SuYou 会额外留一个 237x39 @(-32000,-32000) 的隐藏小窗（最小化后残留），
   * 抓到它就会对着一个不存在的画面 OCR，然后得出"没有候选"这种假结论。 */
  const win = (wins0.windows ?? [])
    .filter((w) => String(w.class ?? '').includes('Tauri'))
    .filter((w) => w.width >= 400 && w.height >= 400 && w.x > -10000 && w.y > -10000)
    .sort((a, b) => b.width * b.height - a.width * a.height)[0]
  if (!win) {
    skip('没有 SuYou 窗口，跳过端到端流程')
  } else {
    const exe = String(win.exe).split(/[\\/]/).pop()
    process.env.DSC_ALLOWED_APPS = exe

    /* 先激活目标窗口 —— 截图走的是 CopyFromScreen（抓的是**屏幕上那块**），
     * 窗户被别的窗口盖住时会抓到覆盖者的像素。第一次跑就把我自己这个 DSH 窗口
     * 的聊天文字 OCR 出来了，结论还写成"没有合适的点击靶子"。
     * 点一下标题栏中间是安全的：只激活，不点到按钮。 */
    await by.get('desktop_click').execute({
      hwnd: win.hwnd,
      x: Math.round(win.x + win.width / 2),
      y: win.y + 10,
      observe: false,
    }).catch(() => {})
    await new Promise((r) => setTimeout(r, 400))

    const shot = await by.get('desktop_screenshot').execute({ hwnd: win.hwnd })
    if (shot?.shotId) ok(`截图：${shot.shotId}`)
    else bad('截图没拿到 shotId')

    const cands = await by.get('desktop_visual_candidates').execute({ hwnd: win.hwnd, shotId: shot.shotId })
    const text = cands.result ?? ''
    if (/windows-ocr/.test(text)) ok('视觉候选来自本机 OCR（windows-ocr）')
    else bad(`候选返回异常：${text.slice(0, 160)}`)

    const ids = [...text.matchAll(/#\d+ ([0-9a-f-]{36}) {2}"([^"]*)"/g)].map((mm) => ({ id: mm[1], label: mm[2] }))
    if (ids.length >= 3) ok(`列出 ${ids.length} 个候选`)
    else bad(`候选太少：${ids.length}；返回原文：${text.slice(0, 300).split('\n').join(' | ')}`)

    // 挑一个静态文字当靶子（不碰实时数字）。
    // 用 query 精确问一次，而不是在前 12 条里碰运气 —— 之前就是这样碰出
    // "没有合适的点击靶子"，把一个能验的流程变成跳过。
    let pick = null
    for (const q of ['当前版本', '规则模式', '剩余流量', 'Su You']) {
      const one = await by.get('desktop_visual_candidates').execute({ hwnd: win.hwnd, shotId: shot.shotId, query: q })
      const hit = [...(one.result ?? '').matchAll(/#\d+ ([0-9a-f-]{36}) {2}"([^"]*)"/g)]
        .map((mm) => ({ id: mm[1], label: mm[2] }))[0]
      if (hit) { pick = hit; break }
    }
    if (!pick) {
      skip(`没有合适的点击靶子（候选：${ids.map((c) => c.label.replace(/\s+/g, '')).join(' / ')}）`)
    } else {
      const r = await by.get('desktop_click_candidate').execute({
        hwnd: win.hwnd, shotId: shot.shotId, candidateId: pick.id, observe: false,
      }).catch((e) => ({ err: e.message }))
      const t = r.err ?? r.result ?? r.text ?? ''
      if (/已点击候选/.test(t)) ok(`按候选点击成功：「${pick.label.replace(/\s+/g, '')}」`)
      else bad(`点击失败：${t.slice(0, 200)}`)

      const dm = /桌面 \((\d+),(\d+)\)/.exec(t)
      if (dm) {
        const x = Number(dm[1])
        const y = Number(dm[2])
        const inside = x >= win.x && x < win.x + win.width && y >= win.y && y < win.y + win.height
        if (inside) ok(`落点 (${x},${y}) 在窗口内（125% 缩放下换算正确）`)
        else bad(`落点 (${x},${y}) 跑出窗口 ${win.x},${win.y} ${win.width}x${win.height}`)
      } else {
        skip('返回里没有落点坐标')
      }
    }

    const before = run(helperCopy, ['tree', String(win.hwnd), '40', '--allow', exe],
      { env: { ...process.env, DSC_ALLOWED_APPS: exe } })
    let total = 0
    try { total = JSON.parse(before.stdout).total ?? 0 } catch { /* ignore */ }
    if (total > 0) ok(`读树仍然可用（${total} 个元素）`)
    else skip('读树没读到内容（窗口可能不在前台/已在后台挂起）')
  }
} finally {
  try { rmSync(work, { recursive: true, force: true }) } catch { /* ignore */ }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
