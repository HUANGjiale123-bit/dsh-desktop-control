#!/usr/bin/env node
/* 光标装饰的接线测试：确认"调一次桌面工具 → 活动文件被续上 → 常驻装饰进程起来了"。
 *
 * 不测视觉效果（那只能人眼看），测的是**接线**：
 *   · 工具调用会 touch 活动文件（否则鱼永远不会出现）
 *   · 只会起一个装饰进程（否则会有多个窗口叠在一起、光标状态互相打架）
 *   · 关掉配置时什么都不做（默认不该动用户的光标）
 *
 * 用法：node tools/test-cursor-fx.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync, existsSync, unlinkSync, readdirSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { tempHelper, assertNotLow } from './fixture.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const HELPER = tempHelper()
assertNotLow(HELPER)

const FX_DIR = join(process.cwd(), '.cache', 'fx-state')
const ACTIVE = join(FX_DIR, 'fx.active')
const STOP = join(FX_DIR, 'fx.stop')

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'cursor-fx-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}
mkdirSync(join(tmp, 'assets', 'cursor', 'poses'), { recursive: true })
writeFileSync(join(tmp, 'assets', 'cursor', 'fish-arrow.png'),
  readFileSync(join('plugin', 'assets', 'cursor', 'fish-arrow.png')))
for (const sub of ['poses', 'poses-arrow']) {
  mkdirSync(join(tmp, 'assets', 'cursor', sub), { recursive: true })
  for (const f of readdirSync(join('plugin', 'assets', 'cursor', sub))) {
    writeFileSync(join(tmp, 'assets', 'cursor', sub, f),
      readFileSync(join('plugin', 'assets', 'cursor', sub, f)))
  }
}
mkdirSync(join(tmp, 'bin'), { recursive: true })
writeFileSync(join(tmp, 'bin', 'dsc-fx.exe'), readFileSync(join('helper', 'bin', 'dsc-fx.exe')))
writeFileSync(join(tmp, 'bin', 'dsc-guard.exe'), readFileSync(join('helper', 'bin', 'dsc-guard.exe')))

function killFx() {
  spawnSync('taskkill', ['/IM', 'dsc-fx.exe', '/F'], { windowsHide: true })
  for (const f of [STOP, join(FX_DIR, 'fx.blanked')]) {
    try { if (existsSync(f)) unlinkSync(f) } catch { }
  }
}

async function boot({ cursorFx, cursorFxMode }) {
  const reg = []
  const logs = []
  const ctx = {
    logger: { info: (m) => logs.push(String(m)), warn: (m) => logs.push(String(m)), error: () => {} },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    on: () => {},
    get(n) {
      if (n === 'approval') return { request: async () => 'allowed-once' }
      if (n === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
      if (n === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['s1'] }] }
      return undefined
    },
  }
  const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: HELPER, escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots'), cursorFx,
    cursorFxMode, cursorFxDir: FX_DIR,
  })
  return { reg, logs, mod, by: new Map(reg.map((t) => [t.name, t])) }
}

try {
  console.log()
  console.log('── 1. 默认关：不该碰光标，也不该起进程 ──')
  {
    killFx()
    try { if (existsSync(ACTIVE)) unlinkSync(ACTIVE) } catch { }
    const { by } = await boot({ cursorFx: false })
    await by.get('desktop_list_windows').execute({}).catch(() => {})
    await new Promise((r) => setTimeout(r, 400))
    if (!existsSync(ACTIVE)) ok('没有写活动文件（配置关着）')
    else bad('配置关着却写了活动文件')
    const r = spawnSync('tasklist', ['/FI', 'IMAGENAME eq dsc-fx.exe'], { encoding: 'utf8', windowsHide: true })
    if (!/dsc-fx\.exe/.test(String(r.stdout))) ok('没有起装饰进程')
    else bad('配置关着却起了装饰进程')
  }

  console.log()
  console.log('── 2. 打开后：调一次桌面工具就报到 ──')
  {
    killFx()
    try { if (existsSync(ACTIVE)) unlinkSync(ACTIVE) } catch { }
    const { by, logs } = await boot({ cursorFx: true })
    await by.get('desktop_list_windows').execute({}).catch(() => {})
    await new Promise((r) => setTimeout(r, 600))

    if (existsSync(ACTIVE)) ok('活动文件已写入（鱼会据此出现）')
    else bad(`活动文件没写（日志：${logs.slice(-2).join(' | ')}）`)

    const r = spawnSync('tasklist', ['/FI', 'IMAGENAME eq dsc-fx.exe'], { encoding: 'utf8', windowsHide: true })
    if (/dsc-fx\.exe/.test(String(r.stdout))) ok('装饰进程已启动')
    else bad(`装饰进程没起来（日志：${logs.slice(-3).join(' | ')}）`)

    const g = spawnSync('tasklist', ['/FI', 'IMAGENAME eq dsc-guard.exe'], { encoding: 'utf8', windowsHide: true })
    if (/dsc-guard\.exe/.test(String(g.stdout))) ok('看门狗已启动')
    else bad('看门狗没起来')

    // 再调一次：不应该起第二个进程
    await by.get('desktop_list_windows').execute({}).catch(() => {})
    await new Promise((r) => setTimeout(r, 300))
    const r2 = spawnSync('tasklist', ['/FI', 'IMAGENAME eq dsc-fx.exe'], { encoding: 'utf8', windowsHide: true })
    const count = (String(r2.stdout).match(/dsc-fx\.exe/g) || []).length
    if (count === 1) ok('只起了一个装饰进程（没有重复启动）')
    else bad(`起了 ${count} 个装饰进程`)
  }
  console.log()
  console.log('── 3. pose 模式：不透明光标，按工具写姿势序号 ──')
  {
    killFx()
    try { if (existsSync(ACTIVE)) unlinkSync(ACTIVE) } catch { }
    const { by } = await boot({ cursorFx: true, cursorFxMode: 'pose' })
    await by.get('desktop_list_windows').execute({}).catch(() => {})
    await new Promise((r) => setTimeout(r, 500))

    const poseFile = join(FX_DIR, 'fx.pose')
    if (existsSync(poseFile)) {
      const n = Number(readFileSync(poseFile, 'utf8').trim())
      // 两批素材的空闲编号不同：带箭头批 = 0（标准箭头），无箭头批 = 5（托腮）。
      // 这个测试的临时目录里两批都有，所以两种都算对。
      if (n === 0 || n === 5) ok(`读取类工具 → 空闲序号 ${n}`)
      else bad(`空闲序号不对：${n}，期望 0 或 5`)
    } else {
      bad('没有写 fx.pose（姿势模式不生效）')
    }

    if (!existsSync(join(FX_DIR, 'fx.blanked'))) ok('没有透明光标（pose 模式不该动系统箭头）')
    else bad('pose 模式却把光标弄透明了')

    const r = spawnSync('tasklist', ['/FI', 'IMAGENAME eq dsc-fx.exe'], { encoding: 'utf8', windowsHide: true })
    if (/dsc-fx\.exe/.test(String(r.stdout))) ok('装饰进程已启动')
    else bad('装饰进程没起来')
  }
} finally {
  killFx()
  // 兜底：万一测试中途把光标弄透明了，立刻恢复
  try {
    spawnSync(HELPER, ['cursor_restore'], { encoding: 'utf8', windowsHide: true })
  } catch { }
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
