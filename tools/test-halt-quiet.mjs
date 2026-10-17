// 用户按 ESC 之后，插件必须**安静地停住**。
//
// ── 这个 bug 是用户实测报上来的 ──
//
// "我在别的窗口使用了下这个插件，我物理按 esc 中止后，其实我的意思就是
//  任务先暂停，但你老是会执拗的打开"
//
// 根因：ensureIndicator() 由 onAuthorized 触发，而它**不检查闸门**。
// 模型每重试一次（动作被闸门拒绝，它会重试），授权走一次，指示层就被
// 重新拉起一次 —— 用户看到屏幕上反复出现"它又活了"。
//
// 所以这个测试守两件事：
//   1. 闸门拉着时，desktop_indicate 必须拒绝
//   2. 闸门拉着时，任何会触发授权的操作都**不能**启动新的 dsc-esc.exe
//
// 用法：node tools/test-halt-quiet.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { execFileSync } from 'node:child_process'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}`); skipped++ }

const tmp = mkdtempSync(join(process.cwd(), '.cache', 'haltquiet-'))
const gate = join(tmp, 'halt')
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

/** 当前有几个 dsc-esc.exe 在跑。 */
function escCount() {
  try {
    const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq dsc-esc.exe', '/NH'], {
      encoding: 'utf8',
      windowsHide: true,
    })
    return (out.match(/dsc-esc\.exe/g) || []).length
  } catch {
    return -1
  }
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
  helperPath: 'helper/bin/dsc.exe',
  escPath: 'helper/bin/dsc-esc.exe',
  gatePath: gate,
  shotDir: join(tmp, 'shots'),
})

const byName = new Map(reg.map((t) => [t.name, t]))
const indicate = byName.get('desktop_indicate')
const click = byName.get('desktop_click')

console.log()
console.log('── 1. 闸门未拉下时，desktop_indicate 正常工作 ──')

try { rmSync(gate, { force: true }) } catch {}
{
  const r = await indicate.execute({}).catch((e) => ({ err: e.message }))
  if (r.err) bad(`闸门未拉下却拒绝了：${r.err.split('\n')[0].slice(0, 70)}`)
  else ok('闸门未拉下时，指示层调用成功')
}

// 等它起来
await new Promise((r) => setTimeout(r, 800))
const before = escCount()
console.log(`  （当前 dsc-esc.exe 进程数：${before}）`)

console.log()
console.log('── 2. 闸门拉下后，desktop_indicate 必须拒绝 ──')

writeFileSync(gate, '测试：模拟用户按 ESC\n2026-10-17 00:00:00\n')
{
  const r = await indicate.execute({}).catch((e) => ({ err: e.message }))
  if (!r.err) {
    bad('闸门拉着，desktop_indicate 却执行了 —— 用户会看到蓝条又冒出来')
  } else if (/ESC 叫停/.test(r.err)) {
    ok('拒绝了，并且说清"用户已按 ESC 叫停"')
  } else {
    ok(`拒绝了（${r.err.split('\n')[0].slice(0, 50)}）`)
  }
  if (r.err && /stop --clear/.test(r.err)) ok('并且告诉了用户怎么解除')
  else if (r.err) bad('拒绝信息里没说怎么解除 —— 用户会不知道怎么办')
}

console.log()
console.log('── 3. 闸门拉着时，授权路径也不能拉起指示层 ──')

// 先记一次数，再让一次会被拒绝的点击走完授权流程
const beforeClick = escCount()
{
  const r = await click
    .execute({ hwnd: 1, x: 10, y: 10, observe: false })
    .catch((e) => ({ err: e.message }))
  // 这里必然失败（窗口不存在 / 急停），关键是它**不能**拉起指示层
  if (r.err) ok(`点击被拒（${r.err.split('\n')[0].slice(0, 46)}）`)
  else bad('闸门拉着，点击居然成功了')
}
await new Promise((r) => setTimeout(r, 900))
const afterClick = escCount()

if (beforeClick < 0 || afterClick < 0) {
  skip('拿不到 dsc-esc.exe 进程数，这条未验证')
} else if (afterClick > beforeClick) {
  bad(
    `授权路径拉起了新的指示层（${beforeClick} → ${afterClick}）` +
      ' —— 这就是用户说的"我按了 ESC，它又执拗地打开"',
  )
} else {
  ok(`没有拉起新的指示层（${beforeClick} → ${afterClick}）`)
}

console.log()
console.log('── 4. 急停生效时，diagnose 不许自相矛盾 ──')

// 实测踩到：闸门拉着时 gate_probe 走 probe-only（不写删，以免覆盖用户写的
// 原因），而 diagnose 把"没做写删测试"当成"未通过"，于是同一个输出里既有
// "急停正在生效"又有"闸门实测未通过 —— ESC 可能只关界面而拦不住操作"。
// 用户按 ESC 之后看到这句会被吓到。
const diagnose = byName.get('desktop_diagnose')
if (!diagnose) {
  skip('没有 desktop_diagnose')
} else {
  const r = await diagnose.execute({}).catch((e) => ({ err: e.message }))
  // ★ 错误消息不是"输出"。上一版直接拿 r.err 当 txt 去匹配，
  //   于是 diagnose 抛错时"没有未通过"这类断言会**假通过**。
  if (r.err) {
    bad(`diagnose 抛错了：${r.err.split(String.fromCharCode(10))[0].slice(0, 90)}`)
  }
  const txt = r.result ?? ''
  if (process.env.DEBUG_HALT) {
    console.log(`      | 输出行数：${txt.split(String.fromCharCode(10)).length}`)
    for (const l of txt.split(String.fromCharCode(10)).slice(0, 6)) {
      console.log(`      | ${l.trim().slice(0, 80)}`)
    }
  }
  // 调试：把含"急停/闸门"的行打出来，跳过时能看出是认错了还是没认
  if (process.env.DEBUG_HALT) {
    for (const l of txt.split('\n')) {
      if (/急停|闸门|解除/.test(l)) console.log(`      | ${l.trim().slice(0, 90)}`)
    }
    console.log(`      | 写测试闸门的路径：${gate}`)
    console.log(`      | 闸门文件存在：${existsSync(gate)}`)
  }
  if (/急停\*\*正在生效\*\*/.test(txt)) ok('diagnose 报告了急停生效')
  else skip('diagnose 里没写急停生效（可能版本不同）')

  if (/闸门实测未通过/.test(txt)) {
    bad('闸门拉着时 diagnose 仍报"实测未通过" —— 和"急停正在生效"自相矛盾')
  } else {
    ok('没有报"实测未通过"（probe-only 不再被当成失败）')
  }
}

console.log()
console.log('── 5. 测试自身的完整性 ──')

{
  const src = readFileSync('plugin/index.mjs', 'utf8')
  if (/function isHalted\(\)/.test(src)) ok('isHalted() 存在')
  else bad('没有 isHalted()')
  if (/if \(isHalted\(\)\) \{[\s\S]{0,120}?halted: true/.test(src)) ok('ensureIndicator 检查闸门')
  else bad('ensureIndicator 没有检查闸门 —— 用户报的那个 bug 会复发')
  if (/急停\*\*正在生效\*\*/.test(src)) ok('diagnose 会显示急停状态并给出解除方式')
  else bad('diagnose 没提急停状态')
}

try { rmSync(tmp, { recursive: true, force: true }) } catch {}

console.log()
console.log('══════════════════════════════')
console.log(
  fail === 0
    ? `  ${pass} 项通过` + (skipped ? `，${skipped} 项跳过（未验证）` : '')
    : `  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过  ← 先修`,
)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
