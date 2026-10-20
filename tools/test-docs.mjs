#!/usr/bin/env node
/* 把 docs 审计接进测试套件：文档与代码不一致时，提交会被拦下。
 *
 * 背景：安装说明连着三版写错（编命令、编路径、漏步骤、声称急停会自动解除）。
 * 这些错误人工评审能抓到一次两次，但抓不住"下一次"——所以让机器每次提交都查。
 *
 * 真正的检查在 tools/audit-docs.mjs：它拿 plugin/index.mjs 里注册的工具名、
 * Program.cs 里的子命令、package.json 里的版本与 files 清单，去比对文档里写的东西。
 *
 * 用法：node tools/test-docs.mjs
 */

import { spawnSync } from 'node:child_process'

console.log()
console.log('── 文档里写的东西，代码里都有吗 ──')

const r = spawnSync(process.execPath, ['tools/audit-docs.mjs'], {
  encoding: 'utf8', windowsHide: true,
})
const out = `${r.stdout ?? ''}${r.stderr ?? ''}`

// 把审计的明细也打出来，失败时人一眼能看到是哪一行
for (const line of out.split('\n')) {
  if (line.trim()) console.log(line)
}

const ok = r.status === 0
if (ok) console.log('  ✓ 文档与代码一致')
else console.log('  ✗ 文档与代码不一致（上面列出的行需要修）')

console.log()
console.log('══════════════════════════════')
console.log(`  ${ok ? 1 : 0} 项通过，${ok ? 0 : 1} 项失败`)
console.log('══════════════════════════════')
process.exit(ok ? 0 : 1)
