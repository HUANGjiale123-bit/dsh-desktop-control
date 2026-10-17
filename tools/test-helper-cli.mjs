// helper / dsc-esc 的【命令行行为】回归测试。
//
// 这三个 bug 都是第三方实测发现的，而且都属于"看起来对、实际不对"：
//
//   ① `dsc.exe stop` 无参数时**写闸门**——查询命令带写副作用。
//      后果：用户按提示跑 `stop` 看状态，反而把闸门拉上了，
//      现场表现是"解除 → 立刻又被拉下"，被误诊成 ESC 硬件抖动。
//   ② `press_key` 的 `chord` 回显了**窗口句柄**——参数取用其实是对的，
//      只有回显错了，于是现象像"参数错位"，把排查方向带骗了。
//   ③ `dsc-esc --timeout` 不存在——`desktop_indicate` 承诺了自动消失
//      却在返回文案里确认一个没实现的承诺。
//
// 这三条都不是"崩溃"，而是**静默的错误语义**，只能靠行为断言守住。
//
// 用法：node tools/test-helper-cli.mjs [helper 目录]

import { spawnSync, spawn } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'

const HELPER_DIR = process.argv[2] ?? 'helper/bin'
const DSC = join(HELPER_DIR, 'dsc.exe')
const ESC = join(HELPER_DIR, 'dsc-esc.exe')

// 本插件自己的窗口（dsc-esc 的全屏光带、逃生条）不该被当成"要操作的目标"：
// 它们的尺寸满足"够大的窗口"，但 dsc-esc 不在白名单里，选中必然被拒，
// 而报出来的错会指向完全无关的地方（踩过：五条 press_key 断言全红）。
//
// 注意分隔符必须同时认 `/` 和 `\`：Windows 路径用的是反斜杠，
// 只写 /[\/]/ 的话 split 切不开，pop 拿到整条路径，isOurs 恒为 false ——
// 过滤器看着在，实际什么也没滤掉（这个写法我第一版就写错了）。
// 刻意保持单行：tools/test-isourwindow.mjs 会把这行取出来验证，跨行会让提取失败。
const isOurs = (w) => /^(dsc|dsc-esc)\.exe$/i.test(String(w.exe ?? '').split(/[\\/]/).pop())

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

// 用独立的临时闸门，绝不碰真实闸门——这个测试本身不能触发急停。
//
// ★ 必须放在【工作区内】，不能用 os.tmpdir()。
//   dsc.exe 是 DSH 的沙箱子进程，**只能写会话工作区**；
//   把闸门放到 %TEMP% 会让 halt 写不进去，于是测出来的"halt 失败"
//   其实是沙箱的拒绝，而不是 halt 坏了。
//   第一版就是这么错的：三条断言全红，看着像急停机制被我改坏了。
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'cli-test-'))
const GATE = join(tmp, 'halt')

function run(exe, args, env = {}) {
  const r = spawnSync(exe, args, {
    encoding: 'utf8',
    env: { ...process.env, DSC_HALT_GATE: GATE, ...env },
    timeout: 15000,
  })
  let json = null
  try { json = JSON.parse((r.stdout ?? '').trim()) } catch { }
  return { json, stdout: r.stdout ?? '', stderr: r.stderr ?? '', code: r.status }
}

console.log()
console.log(`  闸门（临时）：${GATE}`)

if (!existsSync(DSC)) {
  console.log(`  ✗ 找不到 ${DSC}`)
  process.exit(1)
}

console.log()
console.log('── 1. `dsc.exe stop` 无参数必须【只读】（缺陷 2 回归）──')

run(DSC, ['stop', '--clear'])   // 先清干净
let q1 = run(DSC, ['stop'])
if (!existsSync(GATE)) ok('stop 后闸门仍然不存在（没有写副作用）')
else bad('stop 写了闸门 —— 查询命令带写副作用，用户会踩死循环')

if (q1.json?.mode === 'query') ok('mode 报 query')
else bad(`mode 应为 query，实际 ${q1.json?.mode}`)

if (!('cleared' in (q1.json ?? {}))) ok('查询模式不输出 cleared（不会与"解除失败"混淆）')
else bad('查询模式输出了 cleared，容易误读')

// 连跑三次，确认不是偶发
let leaked = false
for (let i = 0; i < 3; i++) {
  run(DSC, ['stop'])
  if (existsSync(GATE)) leaked = true
}
if (!leaked) ok('连续 3 次 stop 都没有留下闸门')
else bad('连续调用中有一次写了闸门')

console.log()
console.log('── 2. `dsc.exe halt` 仍然能拉闸（别把急停修坏了）──')

const h = run(DSC, ['halt', 'cli-test'])
if (existsSync(GATE)) ok('halt 拉下了闸门')
else bad('halt 没能拉下闸门 —— 急停机制被改坏')

const q2 = run(DSC, ['stop'])
if (q2.json?.halted === true) ok('stop 能正确报告"已拉下"')
else bad(`halted 应为 true，实际 ${q2.json?.halted}`)

const c = run(DSC, ['stop', '--clear'])
if (!existsSync(GATE) && c.json?.cleared === true) ok('stop --clear 解除并回读确认')
else bad('stop --clear 没有解除（或 cleared 不为 true）')

console.log()
console.log('── 3. `press_key` 的 chord 回显必须是按键，不是句柄（缺陷 3 回归）──')

// 要一个真实窗口句柄才能过白名单校验；拿不到就跳过这条。
//
// **优先选前台窗口**：press_key 要求目标在前台，否则 helper 会去激活它，
// 而激活在受限环境里可能失败 —— 那种失败与 press_key 本身无关，
// 却会以"chord 回显错误"的形式报出来，指向完全错误的地方。
// 这个测试之前就是因此 flaky：挑中的窗口恰好不是前台时五条断言全红。
let hwnd = 0
const lw = run(DSC, ['list_windows'])
if (Array.isArray(lw.json?.windows)) {
  const usable = (w) => w.width > 300 && w.height > 300 && w.exe && !isOurs(w)
  const pick =
    lw.json.windows.find((w) => w.foreground && usable(w)) ??
    lw.json.windows.find(usable)
  if (pick) hwnd = pick.hwnd
}
if (hwnd) {
  const exe = (lw.json.windows.find((w) => w.hwnd === hwnd)?.exe ?? '').split(/[\\/]/).pop().toLowerCase()
  const pk = run(DSC, ['press_key', String(hwnd), 'Ctrl+A'], { DSC_ALLOWED_APPS: exe })

  // 环境限制（无法激活）与真的坏了要分开：前者跳过，后者才算失败。
  const envBlocked = /无法把窗口.*激活|拒绝访问/.test(pk.json?.message ?? '')
  if (envBlocked) {
    console.log(`  · 目标窗口无法被激活（环境限制），这几条跳过：${(pk.json?.message ?? '').slice(0, 50)}…`)
  } else {
    if (pk.json?.chord === 'Ctrl+A') ok('chord 回显 Ctrl+A（不是句柄）')
    else bad(`chord 回显错误：${JSON.stringify(pk.json?.chord)}（期望 "Ctrl+A"）`)

    if (pk.json?.vk === '0x41') ok('报告了主键虚拟键码 vk=0x41')
    else bad(`vk 不对：${pk.json?.vk}`)

    if (pk.json?.mods === '0x11') ok('报告了修饰键 mods=0x11（Ctrl）')
    else bad(`mods 不对：${pk.json?.mods}`)

    if (pk.json?.verified === false) ok('明确标注 verified=false（不谎报已核实）')
    else bad('verified 字段缺失或不正确')

    if (typeof pk.json?.sentToTarget === 'boolean') ok('报告了 sentToTarget（键发给了哪个窗口）')
    else bad('缺少 sentToTarget')
  }
} else {
  console.log('  （跳过：找不到可用窗口）')
}

console.log()
console.log('── 4. `dsc-esc --timeout` 到期自动收，且【不写闸门】（缺陷 7 回归）──')

if (!existsSync(ESC)) {
  console.log(`  （跳过：找不到 ${ESC}）`)
} else {
  rmSync(GATE, { force: true })
  await new Promise((done) => {
    const child = spawn(ESC, [GATE, '--timeout', '1500'], { stdio: 'ignore', windowsHide: true })
    let exited = false
    const t = setTimeout(() => {
      if (!exited) {
        try { child.kill() } catch { }
        bad('1.5 秒超时设置下，4 秒后进程仍在 —— 超时没生效')
        done()
      }
    }, 4000)
    child.on('exit', () => {
      exited = true
      clearTimeout(t)
      ok('超时后进程自己退出了')
      done()
    })
  })
  if (!existsSync(GATE)) ok('超时**没有**写闸门（收界面 ≠ 停止）')
  else bad('超时写了闸门 —— 用户没按停止却留下闸门，下次操作会被莫名拒绝')
}

console.log()
console.log('── 5. 截图必须报告遮挡（缺陷 5 回归）──')

// 截图走 CopyFromScreen，抓的是**屏幕区域**而不是窗口内容。
// 窗口被盖住时截到的是遮挡者的画面，而且不报错 —— 第三方实测因此
// 差点得出"QQ音乐的界面是 DSH"的错误结论。所以必须带 occluded 字段。
const shot = join(tmp, 'shot.png')
{
  const wins = (run(DSC, ['list_windows']).json?.windows ?? []).filter(
    (w) => w.width > 300 && w.height > 300 && !isOurs(w),
  )
  const fg = wins.find((w) => w.foreground)
  const notFg = wins.filter((w) => !w.foreground)

  if (fg) {
    const exe = (fg.exe ?? '').split(/[\\/]/).pop().toLowerCase()
    const s = run(DSC, ['screenshot', String(fg.hwnd), shot], { DSC_ALLOWED_APPS: exe })
    if (typeof s.json?.occluded === 'boolean') ok('截图返回 occluded 字段（布尔）')
    else bad('截图没有返回 occluded 字段 —— 遮挡会被静默忽略')
    if (s.json?.occluded === false) ok('前台窗口报告未遮挡')
    else if (s.json?.occluded === true) {
      console.log(`  · 前台窗口也报遮挡（有置顶窗口压着）：${s.json?.occludedPoints}`)
    }
  } else {
    console.log('  （跳过：没有前台窗口）')
  }

  if (notFg.length > 0) {
    const w = notFg[0]
    const exe = (w.exe ?? '').split(/[\\/]/).pop().toLowerCase()
    const s = run(DSC, ['screenshot', String(w.hwnd), shot], { DSC_ALLOWED_APPS: exe })
    if (typeof s.json?.occluded === 'boolean') {
      ok(`非前台窗口也返回 occluded 字段（${s.json.occluded ? '遮挡' : '未遮挡'}）`)
      if (s.json.occluded) {
        if (s.json.occludedPoints) ok('遮挡时给出了具体是哪个窗口盖住了')
        else bad('报告了 occluded 却没说是谁挡的 —— 无法据此处理')
      }
    } else {
      bad('非前台窗口截图没有 occluded 字段')
    }
  }
}

// 清理
try { rmSync(tmp, { recursive: true, force: true }) } catch { }

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
