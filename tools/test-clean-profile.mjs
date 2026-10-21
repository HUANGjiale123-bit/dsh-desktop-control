#!/usr/bin/env node
/* 干净 profile 的装卸验证：真实 tgz + 真实 pnpm + 带空格/中文的路径。
 *
 * 为什么单独有这条：复核要求"最终安装包跑完回归和干净 profile 装卸验证"。
 * 前面所有测试都是在插件源码目录上跑的，这条测的是**打出来的包**：
 *
 *   建空 profile → pnpm add file:<tgz> → 登记 bundle（嵌套结构）→ 校验
 *   → 从 node_modules 里真正 import 插件、注册工具 → pnpm remove → 确认干净
 *
 * 它不等于"在真实 DSH 里重启一次"，那一层需要在别人的机器上做；
 * 但包结构、依赖登记、可加载性、卸载残留这几件事它都能抓住。
 *
 * 用法：node tools/test-clean-profile.mjs
 */

import { existsSync, readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { join, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const NODE = process.execPath
const PNPM = process.env.DSC_PNPM || join(process.cwd(), '..', '.dsh', 'pnpm.cjs')

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

/** 找到 pnpm：优先环境变量，其次 DSH runtime 里的那个。 */
function findPnpm() {
  if (process.env.DSC_PNPM && existsSync(process.env.DSC_PNPM)) return process.env.DSC_PNPM
  const candidates = [
    join(process.env.USERPROFILE ?? '', '.dsh', 'dsh-runtimes', 'dsh-primary-runtime',
      'dependencies', 'pnpm', 'bin', 'pnpm.cjs'),
  ]
  for (const c of candidates) if (existsSync(c)) return c
  return null
}

const pnpm = findPnpm()
const tgz = readdirSync(join(process.cwd(), 'dist'))
  .filter((f) => f.endsWith('.tgz'))
  .sort()
  .pop()

console.log()
if (!pnpm) {
  console.log('  · 找不到 pnpm，跳过（设 DSC_PNPM 指向 pnpm.cjs 可启用）')
  process.exit(0)
}
if (!tgz) {
  console.log('  · dist/ 里没有 tgz，跳过')
  process.exit(0)
}
const TARBALL = resolve(join(process.cwd(), 'dist', tgz))
console.log(`  包：${tgz}`)
console.log(`  pnpm：${pnpm}`)

// 带空格 + 中文的目录：验证路径处理
const profileDir = join(process.cwd(), '.cache', '干净 profile 测试')
mkdirSync(profileDir, { recursive: true })
writeFileSync(join(profileDir, 'package.json'), JSON.stringify({ name: 'clean-profile-test', private: true }, null, 2) + '\n')

const run = (args, opts = {}) =>
  spawnSync(NODE, [pnpm, ...args], { cwd: profileDir, encoding: 'utf8', windowsHide: true, timeout: 180000, ...opts })

const readManifest = () => JSON.parse(readFileSync(join(profileDir, 'package.json'), 'utf8'))
const kb = (p) => { try { return Math.round(readFileSync(p).length / 1024) } catch { return 0 } }

let mod = null
try {
  console.log()
  console.log('── 1. 安装：pnpm add file:<tgz> ──')
  const add = run(['add', `file:${TARBALL}`])
  if (add.status === 0) ok('pnpm add 成功')
  else bad(`pnpm add 失败：${String(add.stderr || add.stdout).slice(0, 200)}`)

  const pkgDir = join(profileDir, 'node_modules', 'dsh-desktop-control')
  if (existsSync(join(pkgDir, 'package.json'))) ok('node_modules 里出现了包')
  else bad('node_modules 里没有包')

  const m = readManifest()
  if (m.dependencies && m.dependencies['dsh-desktop-control']) ok('dependencies 已写入')
  else bad('dependencies 没有写入')

  console.log()
  console.log('── 2. 包完整性：bin / 素材 / 版本 ──')
  const installed = JSON.parse(readFileSync(join(pkgDir, 'package.json'), 'utf8'))
  const wantVersion = tgz.match(/(\d+\.\d+\.\d+)/)?.[1]
  if (installed.version === wantVersion) ok(`版本一致：${installed.version}`)
  else bad(`版本不一致：包内 ${installed.version}，文件名 ${wantVersion}`)
  for (const exe of ['dsc.exe', 'dsc-esc.exe', 'dsc-fx.exe', 'dsc-guard.exe']) {
    if (existsSync(join(pkgDir, 'bin', exe))) ok(`${exe} 存在（${kb(join(pkgDir, 'bin', exe))} KB）`)
    else bad(`${exe} 缺失`)
  }
  const poses = join(pkgDir, 'assets', 'cursor', 'poses-arrow')
  const pngs = existsSync(poses) ? readdirSync(poses).filter((f) => f.endsWith('.png')).length : 0
  if (pngs === 10) ok('光标素材 10 张')
  else bad(`光标素材 ${pngs} 张（应为 10）`)

  console.log()
  console.log('── 3. 登记 bundle（宿主读的嵌套结构）──')
  m.dsh = m.dsh ?? {}
  m.dsh.profile = m.dsh.profile ?? {}
  m.dsh.profile.bundles = [...(m.dsh.profile.bundles ?? []), 'dsh-desktop-control']
  writeFileSync(join(profileDir, 'package.json'), JSON.stringify(m, null, 2) + '\n')
  const parsed = JSON.parse(readFileSync(join(profileDir, 'package.json'), 'utf8'))
  if (parsed.dsh.profile.bundles.includes('dsh-desktop-control')) ok('bundle 登记在正确层级')
  else bad('bundle 没登记到 dsh.profile.bundles')

  console.log()
  console.log('── 4. 从装好的位置真正加载插件 ──')
  const reg = []
  const ctx = {
    logger: { info: () => {}, warn: () => {}, error: () => {} },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    on: () => {},
    get() { return undefined },
  }
  mod = await import(pathToFileURL(join(pkgDir, 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, {
    helperPath: join(pkgDir, 'bin', 'dsc.exe'),
    escPath: join(pkgDir, 'bin', 'dsc-esc.exe'),
    shotDir: join(profileDir, 'shots'),
  })
  if (reg.length === 25) ok(`注册了 25 个工具`)
  else bad(`注册了 ${reg.length} 个工具（应为 25）`)
  if (reg.some((t) => t.name === 'desktop_diagnose')) ok('desktop_diagnose 在')
  else bad('desktop_diagnose 缺失')

  console.log()
  console.log('── 5. 卸载：先从 bundles 移除引用，再 pnpm remove（文档顺序）──')
  const beforeRm = readManifest()
  beforeRm.dsh.profile.bundles = (beforeRm.dsh.profile.bundles ?? [])
    .filter((b) => b !== 'dsh-desktop-control')
  writeFileSync(join(profileDir, 'package.json'), JSON.stringify(beforeRm, null, 2) + '\n')
  const noRef = readManifest()
  if (!(noRef.dsh.profile.bundles ?? []).includes('dsh-desktop-control')) ok('bundle 引用已移除')
  else bad('bundle 引用仍在')

  const rm = run(['remove', 'dsh-desktop-control'])
  if (rm.status === 0) ok('pnpm remove 成功')
  else bad(`pnpm remove 失败：${String(rm.stderr || rm.stdout).slice(0, 160)}`)
  if (!existsSync(pkgDir)) ok('包目录已移除')
  else bad('包目录仍在')
  const after = readManifest()
  if (!after.dependencies || !after.dependencies['dsh-desktop-control']) ok('dependencies 已清理')
  else bad('dependencies 仍有残留')
  if (!(after.dsh?.profile?.bundles ?? []).includes('dsh-desktop-control')) ok('卸载后 bundles 不含该插件（宿主可正常重启的必要条件）')
  else bad('卸载后 bundles 仍有悬空引用')
} finally {
  // 清理：只删本测试造的 profile
  try { rmSync(profileDir, { recursive: true, force: true }) } catch { }
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
