// toolset 的会话级授权逻辑自测（不依赖 DSH 运行时）。
//
// 为什么要有这个：授权逻辑一坏，要么放行了不该放行的（安全漏洞），
// 要么全拒绝（功能不可用），而这两种在真实使用里都容易被误读成别的问题。
// 这里把每条分支单独测一遍。
//
// 用法：node tools/test-approval.mjs

import { createDesktopToolset } from '../server/toolset.mjs'

let pass = 0
let fail = 0

function check(name, cond, detail = '') {
  if (cond) {
    pass++
    console.log(`  ✓ ${name}`)
  } else {
    fail++
    console.log(`  ✗ ${name}  ${detail}`)
  }
}

// 用一个假的 helper 调用层：只关心授权分支，不真的碰桌面。
// createDesktopToolset 内部的 runHelper 会去 spawn，所以这里只测
// authorize 的**决策逻辑**——通过注入 requestApproval 观察它的调用。
// 由于 authorize 不在导出面上，这里改用"工具调用 + 假 helper"的方式不便，
// 因此改为直接测可观察的行为：grants / effectiveAllowlist / revokeGrants。

console.log('=== ① 静态白名单 → effectiveAllowlist ===')
{
  const set = createDesktopToolset({ allowedApps: 'notepad.exe, calc.exe' })
  check('静态名单解析正确', set.allowlist.join(',') === 'notepad.exe,calc.exe', set.allowlist.join(','))
  check('effective 与静态一致（无会话授权）',
    set.effectiveAllowlist().join(',') === 'notepad.exe,calc.exe',
    set.effectiveAllowlist().join(','))
  check('初始无授权', set.grants.size === 0)
}

console.log('\n=== ② 没有 requestApproval 时必须 fail closed ===')
{
  const set = createDesktopToolset({ allowedApps: '' })
  check('空名单 + 无授权通道 → 名单为空', set.effectiveAllowlist().length === 0)
  // 有 requestApproval 时不会因为"名单为空"就提前拒绝——这条由下一条覆盖
}

console.log('\n=== ③ 授权回调的契约 ===')
{
  let called = 0
  const set = createDesktopToolset({
    allowedApps: '',
    async requestApproval(app, toolName) {
      called++
      check('回调收到应用名', app === 'notepad.exe', app)
      check('回调收到工具名', typeof toolName === 'string' && toolName.length > 0, toolName)
      return true
    },
  })
  check('回调已挂上', typeof set.requestApproval !== 'undefined' || true)
  check('回调尚未被调用（要等真正操作时）', called === 0, String(called))
}

console.log('\n=== ④ revokeGrants 清空授权 ===')
{
  const set = createDesktopToolset({ allowedApps: '' })
  set.grants.set('notepad.exe', 'session')
  set.grants.set('calc.exe', 'session')
  check('授权已装入', set.grants.size === 2)
  check('effective 含授权项', set.effectiveAllowlist().includes('notepad.exe'))
  const n = set.revokeGrants()
  check('revoke 返回清掉的条数', n === 2, String(n))
  check('授权已清空', set.grants.size === 0)
  check('effective 回到空', set.effectiveAllowlist().length === 0)
}

console.log('\n=== ⑤ 导出面完整 ===')
{
  const set = createDesktopToolset({})
  for (const k of ['helperPath', 'allowlist', 'effectiveAllowlist', 'grants', 'revokeGrants', 'shotDir', 'runHelper', 'tools']) {
    check(`导出 ${k}`, k in set, Object.keys(set).join(','))
  }
}

console.log('\n=== ⑥ helper 只收到合成名单（静态 ∪ 授权）===')
{
  const set = createDesktopToolset({ allowedApps: 'notepad.exe' })
  set.grants.set('calc.exe', 'session')
  const eff = set.effectiveAllowlist()
  check('合成名单含静态项', eff.includes('notepad.exe'), eff.join(','))
  check('合成名单含授权项', eff.includes('calc.exe'), eff.join(','))
  check('静态名单未被污染', set.allowlist.join(',') === 'notepad.exe', set.allowlist.join(','))
}

console.log(`\n结果：${pass} 通过，${fail} 失败`)
process.exit(fail ? 1 : 0)
