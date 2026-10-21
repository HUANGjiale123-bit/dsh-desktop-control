#!/usr/bin/env node
/* 看门狗：装饰进程被强杀时，光标必须被恢复。
 *
 * 这是整个光标功能里**唯一会让用户失去光标**的故障路径：SetSystemCursor 改的是
 * 系统全局资源，进程一死不会自动还原。所以这条必须有回归测试，不能靠"我记得能行"。
 *
 * 测试会真的把光标变透明几秒，任何失败路径最后都会调一次 cursor_restore 兜底。
 *
 * 用法：node tools/test-cursor-guard.mjs
 */

import { existsSync, readFileSync, writeFileSync, rmSync, mkdirSync, unlinkSync, copyFileSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { tempHelper, assertNotLow } from './fixture.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const HELPER = tempHelper()
assertNotLow(HELPER)

const RUN = 'guard-test-' + Date.now()
const DIR = join(tmpdir(), 'dsc-guard-test')
const MARK = join(DIR, 'fx.blanked')
const LOG = join(DIR, 'guard.log')
const ACTIVE = join(DIR, 'fx.active')
const STOP = join(DIR, 'fx.stop')
// 必须像生产那样先复制到 %TEMP% 再跑：工作区带 Low 完整性标签，
// 从那里起的进程**没有权限**往 %TEMP% 写 marker（实测 UnauthorizedAccessException）。
const FX_SRC = join(process.cwd(), 'helper', 'bin', 'dsc-fx.exe')
const GUARD_SRC = join(process.cwd(), 'helper', 'bin', 'dsc-guard.exe')
const FX = join(DIR, 'dsc-fx.exe')
const GUARD = join(DIR, 'dsc-guard.exe')
const ASSET = join(process.cwd(), 'plugin', 'assets', 'cursor', 'fish-arrow.png')

function restore() {
  try { spawnSync(HELPER, ['cursor_restore'], { encoding: 'utf8', windowsHide: true }) } catch { }
}

function clean() {
  spawnSync('taskkill', ['/IM', 'dsc-fx.exe', '/F'], { windowsHide: true })
  spawnSync('taskkill', ['/IM', 'dsc-guard.exe', '/F'], { windowsHide: true })
  for (const f of [MARK, LOG, ACTIVE, STOP]) {
    try { if (existsSync(f)) unlinkSync(f) } catch { }
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

try {
  mkdirSync(DIR, { recursive: true })
  copyFileSync(FX_SRC, FX)
  copyFileSync(GUARD_SRC, GUARD)
  clean()
  restore()

  console.log()
  console.log('── 1. 装饰进程正常时：看门狗不插手 ──')
  {
    // 先起看门狗：装饰进程要看到 guard.ready 才允许隐藏系统光标
    // （复核 2.18.71：没有恢复者时不能隐藏光标）。
    const guard = spawn(GUARD, ['--watch', DIR, '--run', RUN], { stdio: 'ignore', windowsHide: true })
    for (let i = 0; i < 20 && !existsSync(join(DIR, 'guard.ready')); i++) await sleep(100)

    const errFile = join(DIR, 'fx.err')
    const fx = spawn(FX, ['--watch', DIR, 'blank', '120', `${ASSET}@45,102`, '--run', RUN], {
      stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true,
    })
    let fxErr = ''
    fx.stderr.on('data', (d) => { fxErr += String(d) })
    fx.on('exit', (code) => { try { writeFileSync(errFile, `code=${code}
${fxErr}`) } catch { } })
    await sleep(800)
    if (!existsSync(ACTIVE)) writeFileSync(ACTIVE, String(Date.now()))
    await sleep(1200)

    if (existsSync(MARK)) ok('装饰进程已把光标变透明（marker 存在）')
    else {
      let e = ''
      try { e = readFileSync(errFile, 'utf8') } catch { }
      bad(`装饰进程没写 marker，后面的判断无从谈起（${e.slice(0, 160).split('\n').join(' ')}）`)
    }

    await sleep(1500)
    if (existsSync(MARK)) ok('看门狗运行期间没有误删 marker')
    else bad('看门狗在装饰进程还活着时就把 marker 删了')

    console.log()
    console.log('── 2. 强杀装饰进程：看门狗必须恢复光标 ──')
    spawnSync('taskkill', ['/IM', 'dsc-fx.exe', '/F'], { windowsHide: true })

    let restoredAt = -1
    const start = Date.now()
    for (let i = 0; i < 40; i++) {
      await sleep(150)
      if (!existsSync(MARK)) { restoredAt = Date.now() - start; break }
    }

    if (restoredAt >= 0) ok(`光标已恢复（用时 ${restoredAt}ms）`)
    else bad('5 秒内没有恢复 —— 用户会失去光标')

    if (restoredAt >= 0 && restoredAt < 3000) ok('恢复足够快（<3 秒）')
    else if (restoredAt >= 0) bad(`恢复太慢：${restoredAt}ms`)

    let logged = ''
    try { logged = readFileSync(LOG, 'utf8') } catch { }
    if (/恢复光标/.test(logged)) ok('看门狗写了恢复日志（可追溯）')
    else bad(`没有恢复日志：${logged.slice(0, 120)}`)

    try { guard.kill() } catch { }
  }

  console.log()
  console.log('── 3. 正常收工：stop 之后看门狗自己退出 ──')
  {
    clean()
    restore()
    writeFileSync(STOP, '1')
    const guard = spawn(GUARD, ['--watch', DIR], { stdio: 'ignore', windowsHide: true })
    let exited = false
    guard.on('exit', () => { exited = true })
    const start = Date.now()
    while (Date.now() - start < 12000) {
      await sleep(300)
      if (exited) break
    }
    if (exited) ok(`收到 fx.stop 后退出（${Date.now() - start}ms）`)
    else {
      bad('收到 fx.stop 后没有退出（会一直挂着）')
      try { guard.kill() } catch { }
    }
  }
} finally {
  clean()
  restore()
  rmSync(DIR, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
