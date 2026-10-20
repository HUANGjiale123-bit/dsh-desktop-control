#!/usr/bin/env node
/* 守住"打包后的插件必须能跑"。
 *
 * ── 为什么要这个测试 ──
 *
 * 有两个 bug 只在"从 tgz 安装"时才暴露，而本机一直是从源码目录加载的，
 * 所以它们活了很久没人发现：
 *
 *   ① REPO_ROOT = HERE/..，在仓库里是仓库根，在打包后是 node_modules。
 *      于是 dsc.exe 找成 node_modules/helper/bin/dsc.exe —— 那个路径不存在，
 *      所有工具一启动就 HelperMissing。
 *   ② sync-plugin.sh 的文件清单里没有 uia-bridge.py，打包版因此没有读取桥。
 *      而这个失败是静默的：readWindow 照常返回，只是内容变回外壳。
 *
 * 这个测试不碰桌面：它只检查**打包结构**和**路径解析**。真读窗口的验证
 * 在 test-bridge-chromium.mjs 里。
 */

import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve, sep } from 'node:path'
import { readdirSync } from 'node:fs'

const results = []
let pass = 0
let fail = 0
const ok = (m) => { results.push(`  ✓ ${m}`); pass++ }
const bad = (m) => { results.push(`  ✗ ${m}`); fail++ }

const repo = resolve(import.meta.dirname, '..')
const distDir = join(repo, 'dist')

// 找最新的 tgz
const tgz = execFileSync('bash', ['-lc', `ls -t "${distDir}"/*.tgz 2>/dev/null | head -1`], {
  encoding: 'utf8',
}).trim()

if (!tgz) {
  console.log('  ✗ dist/ 里没有 tgz')
  process.exit(1)
}
results.push(`  测的包: ${tgz.split(/[\\/]/).pop()}`)
results.push('')

const tmp = mkdtempSync(join(tmpdir(), 'dsc-tgz-'))
try {
  // tar 在 MSYS 里不认 D:\ 这种 Windows 路径（会把 D: 当主机名），
  // 所以交给 bash 转一次路径。
  const toBash = (p2) => '/' + p2[0].toLowerCase() + p2.slice(2).split('\\').join('/')
  execFileSync('bash', ['-lc',
    'tar -xzf "' + toBash(tgz) + '" -C "' + toBash(tmp) + '"'])

  results.push('── 1. 包里必须有这几样 ──')
  {
    const required = [
      ['index.mjs', '插件入口'],
      ['toolset.mjs', '工具实现'],
      ['bin/dsc.exe', '辅助进程（打包后位置是 bin/，不是 ../helper/bin/）'],
      ['bin/dsc-esc.exe', '急停与光带'],
      ['uia-bridge.py', 'Chromium 读取桥'],
    ]
    for (const [f, why] of required) {
      const p = join(tmp, 'package', f)
      if (existsSync(p)) ok(`${f} —— ${why}`)
      else bad(`${f} 不在包里 —— ${why}`)
    }
  }

  results.push('')
  results.push('── 2. bin/dsc.exe 必须是可执行的非空文件 ──')
  {
    const p = join(tmp, 'package', 'bin', 'dsc.exe')
    if (existsSync(p)) {
      const st = readFileSync(p)
      if (st.length > 10000) ok(`dsc.exe 有 ${st.length} 字节`)
      else bad(`dsc.exe 只有 ${st.length} 字节，可能是占位`)
    }
  }

  results.push('')
  results.push('── 3. 路径解析必须按打包布局，不是仓库布局 ──')
  {
    const ts = readFileSync(join(tmp, 'package', 'toolset.mjs'), 'utf8')
    // 新代码里应该有 HERE/bin 这个候选
    if (ts.includes("join(HERE, 'bin', 'dsc.exe')")) {
      ok("toolset.mjs 里有 HERE/bin/dsc.exe 这个候选（打包布局）")
    } else {
      bad("toolset.mjs 里没有 HERE/bin/dsc.exe —— 打包后会找错 dsc.exe")
    }
    if (ts.includes('function resolveHelperPath')) {
      ok('helperPath 走 resolveHelperPath 按序探测')
    } else {
      bad('helperPath 还是单一路径')
    }
  }

  results.push('')
  results.push('── 4. 桥脚本必须真的在包内被找到 ──')
  {
    const p = join(tmp, 'package', 'uia-bridge.py')
    if (existsSync(p)) {
      const src = readFileSync(p, 'utf8')
      if (src.includes('AutomationElement')) ok('桥脚本内容正确（含 AutomationElement）')
      else bad('桥脚本内容不对')
      if (src.includes("reconfigure(encoding=\"utf-8\"") ||
          src.includes("reconfigure(encoding='utf-8'")) {
        ok('桥脚本强制了 stdout UTF-8（否则中文窗口名会让它静默失败）')
      } else {
        bad('桥脚本没强制 UTF-8 —— 中文场景会 UnicodeEncodeError')
      }
    }
  }

  results.push('')
  results.push('── 5. package.json 的 files 必须与包内内容一致 ──')
  {
    const pj = JSON.parse(readFileSync(join(tmp, 'package', 'package.json'), 'utf8'))
    const files = pj.files ?? []
    if (files.includes('uia-bridge.py')) ok('files 里列了 uia-bridge.py')
    else bad('files 里没有 uia-bridge.py —— 下次 pnpm pack 会漏掉')

    // 版本要和 index.mjs 一致
    const idx = readFileSync(join(tmp, 'package', 'index.mjs'), 'utf8')
    const m = idx.match(/PLUGIN_VERSION = '([0-9.]+)'/)
    if (m && pj.version === m[1]) ok(`版本一致：${pj.version}`)
    else bad(`版本不一致：package.json=${pj.version} index.mjs=${m ? m[1] : '?'}`)
  }
} finally {
  try { rmSync(tmp, { recursive: true, force: true }) } catch { /* 清理失败不影响结论 */ }
}

// ── 素材必须都在打包清单里 ──
//
// 十张光标姿势图曾经没写进 package.json 的 files —— 打包不报错，条目就是没有，
// 装到别人机器上光标功能直接退化（只剩一张图）。这类"少文件但静默"的问题
// 只能靠测试兜住。
{
  const pkg = JSON.parse(readFileSync(join(repo, 'plugin', 'package.json'), 'utf8'))
  const files = pkg.files ?? []
  const pluginRoot = join(repo, 'plugin')
  const missing = []
  const walk = (dir) => {
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      const p = join(dir, e.name)
      if (e.isDirectory()) { walk(p); continue }
      const rel = p.slice(pluginRoot.length + 1).split(sep).join('/')
      const covered = files.some((f) => {
        const norm = String(f).split(sep).join('/').replace(/\/$/, '')
        return rel === norm || rel.startsWith(norm + '/')
      })
      if (!covered) missing.push(rel)
    }
  }
  walk(join(pluginRoot, 'assets'))

  let total = 0
  const count = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      if (e.isDirectory()) count(join(d, e.name))
      else total++
    }
  }
  count(join(pluginRoot, 'assets'))

  if (missing.length === 0) ok(`assets 下 ${total} 个文件都在 files 清单里`)
  else bad(`${missing.length} 个素材不在打包清单：${missing.slice(0, 4).join(', ')}`)
}

console.log(results.join('\n'))
console.log('')
console.log('══════════════════════════════')
if (fail === 0) console.log(`  ${pass} 项通过`)
else console.log(`  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
