// 应用名解析的回归测试。
//
// 这一块出过两个"看起来对、后果严重"的问题：
//
//   ① 模糊匹配会自动启动。清单里没有"微信"（Store 版没有传统卸载项），
//      只有"企业微信"，于是 `微信` 唯一命中"企业微信"并**把它启动了**，
//      还报告成功。想开微信结果开了企业微信。
//   ② 清单指向了卸载程序。剪映专业版 → uninst.exe、Steam → uninstall.exe。
//      照它启动会去卸载软件。
//
// 两条都不是崩溃，都是"静默地把事情做到别人身上"，所以只能靠断言守住。
//
// 用法：node tools/test-apps.mjs

import { resolveApp, listInstalledApps, searchApps } from '../server/apps.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

console.log()
console.log('── 1. 精确匹配必须命中 ──')

{
  // 拿一个清单里真实存在的应用来测，不硬编码具体机器上的名字
  const all = listInstalledApps()
  console.log(`     清单共 ${all.length} 个应用`)
  const sample = all.find((a) => a.name.length > 2)
  if (sample) {
    const r = resolveApp(sample.name)
    if (r.ok && r.app.exe === sample.exe) ok(`显示名精确匹配：${sample.name}`)
    else bad(`显示名精确匹配失败：${sample.name} → ${JSON.stringify(r.reason)}`)

    // exe 文件名也要能匹配（不带路径）
    const base = sample.exe.split(/[\\/]/).pop()
    const r2 = resolveApp(base)
    if (r2.ok && r2.app.exe === sample.exe) ok(`exe 文件名匹配：${base}`)
    else bad(`exe 文件名匹配失败：${base} → ${JSON.stringify(r2.reason)}`)
  }
}

console.log()
console.log('── 2. 模糊匹配【绝不】自动启动（危险 bug 的回归）──')

{
  // "微信" vs "企业微信" 是最典型的案例：前者在清单里通常不存在，
  // 而后者存在。旧实现会唯一命中后者并启动它。
  const all = listInstalledApps()
  const hasWeixin = all.some((a) => a.name === '微信')
  const hasWework = all.some((a) => a.name === '企业微信')

  if (hasWework && !hasWeixin) {
    const r = resolveApp('微信')
    if (!r.ok && r.reason === 'NotExact') {
      ok('「微信」不被解析成「企业微信」，只给候选')
      if ((r.candidates ?? []).some((c) => c.name === '企业微信')) {
        ok('候选里确实列出了「企业微信」，调用方能看到该用什么名字')
      } else {
        bad('候选列表里没有「企业微信」—— 提示没用')
      }
    } else if (r.ok) {
      bad(`「微信」被自动解析成了「${r.app.name}」—— 会启动错误的应用`)
    } else {
      bad(`「微信」的失败原因应为 NotExact，实际 ${r.reason}`)
    }
  } else {
    console.log('  （跳过：本机清单里微信/企业微信的组合不成立）')
  }

  // 通用判据：任何"只是包含关系"的查询都不该返回 ok
  const fuzzyCases = ['微', '腾', 'a', 'e']
  let leaked = []
  for (const q of fuzzyCases) {
    const r = resolveApp(q)
    // 只有当 q 恰好等于某个名字/exe 名时才算合法命中
    if (r.ok) {
      const hit = r.app
      const exact =
        hit.name.toLowerCase() === q.toLowerCase() ||
        hit.exe.split(/[\\/]/).pop().toLowerCase() === q.toLowerCase() + '.exe' ||
        hit.exe.split(/[\\/]/).pop().toLowerCase() === q.toLowerCase()
      if (!exact) leaked.push(`${q} → ${hit.name}`)
    }
  }
  if (leaked.length === 0) ok('单字/短词查询没有一个被自动解析成具体应用')
  else bad(`这些短词被自动解析了：${leaked.join('；')}`)
}

console.log()
console.log('── 3. 清单不能指向卸载/安装程序 ──')

{
  const all = listInstalledApps()
  // 只看**文件名**判据 —— 这也是代码里用的判据。
  // 用完整路径判会误伤 legit 程序（例如 D:\LenovoSoftstore\Install\... 下的主程序）。
  const badEntries = all.filter((a) => {
    const base = a.exe.split(/[\\/]/).pop().toLowerCase()
    return /uninst|setup|install|update|upgrade|repair|crash/.test(base)
  })
  if (badEntries.length === 0) ok(`清单里没有指向卸载/安装程序的条目（共 ${all.length} 个）`)
  else {
    bad(`有 ${badEntries.length} 条指向卸载/安装程序，启动它们会做危险的事：`)
    for (const b of badEntries.slice(0, 5)) console.log(`      ${b.name} → ${b.exe}`)
  }
}

console.log()
console.log('── 4. 不存在的名字 → NoMatch，且不乱给候选 ──')

{
  const r = resolveApp('这个应用绝对不存在xyz123')
  if (!r.ok && r.reason === 'NoMatch') ok('不存在的名字报 NoMatch')
  else bad(`不存在的名字报了 ${r.reason}（应为 NoMatch）`)
  if ((r.candidates ?? []).length === 0) ok('没有乱给候选')
  else bad('不存在却给了候选')
}

console.log()
console.log('── 5. 明确给出的 .exe 路径要能用；不存在的路径要拒绝 ──')

{
  const all = listInstalledApps()
  const sample = all[0]
  if (sample) {
    const r = resolveApp(sample.exe)
    if (r.ok) ok('完整 .exe 路径可直接使用')
    else bad('完整 .exe 路径被拒了')
  }
  const fake = resolveApp('D:/nonexistent/dir/app.exe')
  if (!fake.ok) ok('不存在的 .exe 路径被拒绝')
  else bad('不存在的 .exe 路径被接受了')
}

console.log()
console.log('── 6. searchApps 仍然可以模糊（浏览 ≠ 执行）──')

{
  const hits = searchApps('腾讯', 8)
  if (Array.isArray(hits)) ok(`searchApps 允许模糊（"腾讯"命中 ${hits.length} 个）`)
  else bad('searchApps 失效')
}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
