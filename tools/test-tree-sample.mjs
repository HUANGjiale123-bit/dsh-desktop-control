#!/usr/bin/env node
/* 样本太小的时候，不许对"这棵树有没有信息量"下结论。
 *
 * ── 这是怎么发现的 ──
 *
 * 排查"同一个窗口在 helper 能不能读之间来回翻转"时，数据长这样：
 *   同一分钟、同一个 DSH 窗口，maxNodes=60 → helper 直读 60 个节点；
 *   下一次 maxNodes=8  → 走了 UIA 桥（说明 helper 被判"不可用"）。
 * 看上去像 helper 间歇性失明。实际是**取样太小**：
 * helper 的 named/actionable 只数列出的那些节点（遍历在 maxNodes 处停），
 * maxNodes=8 时前 8 个恰好全是 Pane → named=0 → 判"树不可用"
 * → 插件去换桥，并对模型说"这棵树信息很少，改用截图"。
 *
 * 同一个 bug 在桥那边修过（28e90d7：它数的是自己印出来的行数）。
 * 这里是镜像位置的同一处 —— 修了一边，另一边没跟上。
 *
 * 用自己造的窗体当靶子（12 个带名字的 Label），不碰用户的窗口。
 *
 * 用法：node tools/test-tree-sample.mjs
 */

import { spawn, spawnSync } from 'node:child_process'
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}（未验证）`) }

const PS = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe'

/* 造一个"信息量充足"的窗口：12 个带文字的 Label。
 * 必须是真窗口（不是画布），这样 UIA 才有 Name 可读。 */
const formScript = [
  `Add-Type -AssemblyName System.Windows.Forms`,
  `Add-Type -AssemblyName System.Drawing`,
  `$f = New-Object System.Windows.Forms.Form`,
  `$f.Text = 'dsc-sample-tree'`,
  `$f.StartPosition = 'Manual'`,
  `$f.Location = New-Object System.Drawing.Point(600, 300)`,
  `$f.Size = New-Object System.Drawing.Size(420, 520)`,
  `for ($i = 1; $i -le 12; $i++) {`,
  `  $l = New-Object System.Windows.Forms.Label`,
  `  $l.Text = "样本文本 $i"`,
  `  $l.Location = New-Object System.Drawing.Point(20, (20 + $i * 36))`,
  `  $l.AutoSize = $true`,
  `  $f.Controls.Add($l)`,
  `}`,
  `$f.Show()`,
  `Write-Output ("hwnd=" + $f.Handle)`,
  `$end = (Get-Date).AddSeconds(25)`,
  `while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 80 }`,
  `$f.Close()`,
].join('; ')

let child = null
let hwnd = 0
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'treesample-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

const formOut = await new Promise((resolve) => {
  child = spawn(PS, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-Command', formScript], { stdio: ['ignore', 'pipe', 'pipe'] })
  let buf = ''
  const timer = setTimeout(() => resolve(buf), 8000)
  child.stdout.on('data', (d) => {
    buf += d.toString()
    if (/hwnd=\d+/.test(buf)) { clearTimeout(timer); resolve(buf) }
  })
  child.on('error', () => { clearTimeout(timer); resolve('') })
})

const m = String(formOut).match(/hwnd=(\d+)/)
hwnd = m ? Number(m[1]) : 0

function helperTree(maxNodes) {
  const r = spawnSync('helper/bin/dsc.exe',
    ['tree', String(hwnd), String(maxNodes), '--allow', 'powershell.exe'],
    { encoding: 'utf8', windowsHide: true })
  try { return JSON.parse(String(r.stdout || '{}')) } catch { return { ok: false, error: 'parse' } }
}

try {
  console.log()
  if (!hwnd) {
    console.log('  ✗ 造不出测试窗口（WinForms 窗体没起来），这个测试没法做')
    process.exit(1)
  }
  console.log(`  靶子 hwnd=${hwnd}（自己造的 WinForms 窗体，12 个带文字的 Label）`)

  console.log()
  console.log('── 1. maxNodes=2：样本太小，不许判"不可用" ──')
  {
    const r = helperTree(2)
    if (!r.ok) {
      bad(`读树失败：${r.error}`)
    } else {
      if (r.truncated === true) ok(`确认这次是被截断的（listed=${r.listed}, total=${r.total}）`)
      else bad(`期望 truncated=true，实际 ${r.truncated}`)

      if (r.countsConclusive === false) ok('countsConclusive=false（样本不足以判断）')
      else bad(`countsConclusive=${r.countsConclusive}，应当为 false —— 否则又会按前 2 行下结论`)

      if (r.usable === true) ok('usable=true（不知道就不下结论，而不是当成"树很差"）')
      else bad(`usable=${r.usable} —— maxNodes 小就会被判成"树不可用"，桥与提示都会被错误触发`)

      console.log(`    · named=${r.named} actionable=${r.actionable} roles=${r.roles}`)
    }
  }

  console.log()
  console.log('── 2. maxNodes=200：样本够，结论照旧 ──')
  {
    const r = helperTree(200)
    if (!r.ok) {
      bad(`读树失败：${r.error}`)
    } else {
      if (r.countsConclusive === true) ok('countsConclusive=true')
      else bad(`countsConclusive=${r.countsConclusive}，应当为 true`)

      if ((r.named ?? 0) >= 12) ok(`带名字 ${r.named} 个（12 个 Label 都在）`)
      else bad(`带名字只有 ${r.named} 个，期望 ≥12`)

      if (r.usable === true) ok('usable=true（这棵树确实能用）')
      else bad('usable=false —— 这棵树明明有名字也有可交互元素')
    }
  }

  console.log()
  console.log('── 3. 插件文本不再说"这棵树信息很少" ──')
  {
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
      allowedApps: 'powershell.exe',
      gatePath: join(tmp, 'halt'),
      shotDir: join(tmp, 'shots'),
    })
    const read = reg.find((t) => t.name === 'desktop_read_window')

    // 工具返回的是 { tree }（这个工具的 schema 就叫 tree），不是 toolset 层的 { text }。
    // 第一版读错字段拿到空串，于是"没有出现那句坏话"是**空串上成立的**——
    // 断言太宽松，等于没验。（这个毛病本项目犯过不止一次。）
    const small = String((await read.execute({ hwnd, maxNodes: 2 }).catch((e) => ({ tree: e.message }))).tree ?? '')
    if (!small) {
      bad('maxNodes=2 时返回是空的，这一格没验成')
    } else {
      if (/树信息很少/.test(small)) bad('maxNodes=2 时仍然说"树信息很少，改用截图" —— 一句话把能用的树劝退了')
      else ok('maxNodes=2 时不再说"树信息很少"')

      if (/无法据此判断整棵树的信息量/.test(small)) ok('改成说明"样本不足，调大 maxNodes 再看"')
      else bad(`没看到"样本不足"的说明，文本结尾：${small.slice(-160)}`)
    }

    const big = String((await read.execute({ hwnd, maxNodes: 200 }).catch((e) => ({ tree: e.message }))).tree ?? '')
    if (!big) bad('maxNodes=200 时返回是空的')
    else if (/树信息很少|无法据此判断/.test(big)) bad('maxNodes=200 时不该出现任何"样本不足/信息很少"的提示')
    else ok('maxNodes=200 时没有任何误导性提示')
  }
} finally {
  try { child?.kill() } catch { /* ignore */ }
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
