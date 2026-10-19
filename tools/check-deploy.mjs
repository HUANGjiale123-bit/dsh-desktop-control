// 检查部署目录是否**自洽**：所有相对 import 指向的文件真的存在。
//
// ── 为什么需要它 ──
//
// 出过一次事故：新增 tooldsl.mjs 后，sync-plugin.sh 里的文件列表是硬编码的，
// 没带上新文件。于是部署目录里缺这一个文件，而 index.mjs 里写着
// `import ... from './tooldsl.mjs'` —— Node 直接 ERR_MODULE_NOT_FOUND，
// 模块加载失败，插件激活失败，**所有工具一起消失**。
//
// 这和下午那个"缺 @deepseek-ai/dsh-tools"是同一个失败形状，只是换了个原因。
// 两次都说明：**"文件抄了几个"这种硬编码列表，迟早会和代码脱节。**
//
// 而自检当时没抓到，是因为它验的路径不对：
//   · 验过"从 tgz 解包后能否 import"——那里文件是齐的（pnpm pack 按 files 数组走）
//   · 没验"部署目录本身能否 import"
// 源码目录里文件也齐，所以本机一路都看不出问题。
//
// 用法：node tools/check-deploy.mjs <部署目录>
// 退出码：0 = 自洽，1 = 有缺失/破的引用，2 = 用法或读取错误。

import { readFileSync, existsSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'

const dir = process.argv[2]
if (!dir) {
  console.error('用法：node tools/check-deploy.mjs <部署目录>')
  process.exit(2)
}
if (!existsSync(dir) || !statSync(dir).isDirectory()) {
  console.error(`目录不存在：${dir}`)
  process.exit(2)
}

// 检查入口 + 它可能相对引用的所有模块
const entries = ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs', 'visual-candidates.mjs', 'ocr-worker.mjs']
const problems = []
const checked = []

for (const entry of entries) {
  const file = join(dir, entry)
  if (!existsSync(file)) {
    problems.push(`${entry}: 文件不存在`)
    continue
  }
  let src
  try {
    src = readFileSync(file, 'utf8')
  } catch (e) {
    problems.push(`${entry}: 读不到 (${e.message})`)
    continue
  }

  // 只挑行首的静态 import —— 注释里提到路径不该算
  for (const line of src.split('\n')) {
    const m = /^\s*import\s+.*?\s+from\s+'([^']+)'/.exec(line)
    if (!m) continue
    const spec = m[1]
    if (!spec.startsWith('.')) continue // 裸模块与 node: 内置不在这里管
    const target = resolve(dirname(file), spec)
    checked.push(`${entry} → ${spec}`)
    if (!existsSync(target)) {
      problems.push(`${entry} 引用了 ${spec}，但 ${target} 不存在`)
    }
  }
}

if (problems.length === 0) {
  console.log(`  ✓ 部署目录自洽（${checked.length} 处相对引用全部存在）`)
  for (const c of checked) console.log(`      ${c}`)
  process.exit(0)
}

console.log('  ✗ 部署目录不自洽：')
for (const p of problems) console.log(`      ${p}`)
console.log('      相对 import 指向缺失文件 → 模块加载失败 → 插件激活失败 →')
console.log('      所有工具一起消失。补进 tools/sync-plugin.sh 的文件列表。')
process.exit(1)
