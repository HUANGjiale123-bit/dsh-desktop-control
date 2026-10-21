// 插件自包含性检查：确认装到别人机器上也能找到辅助程序。
//
// 为什么单独测这个：插件要么"装完就能用"，要么要求用户手填路径。
// 后者作为可分发插件是不可接受的——而路径解析出错时的症状是
// "工具报找不到 dsc.exe"，用户无从下手。
//
// 用法：node tools/test-packaging.mjs
//
// 它不启动 DSH，只复现插件里的解析顺序并检查文件确实存在。

import { existsSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = dirname(fileURLToPath(import.meta.url))
const REPO = resolve(HERE, '..')
const pluginDir = join(REPO, 'plugin')

let pass = 0, fail = 0
const check = (name, cond, detail = '') => {
  if (cond) { pass++; console.log(`  ✓ ${name}`) }
  else { fail++; console.log(`  ✗ ${name}  ${detail}`) }
}

console.log('=== ① 插件包自带辅助程序 ===')
const bundled = join(pluginDir, 'bin', 'dsc.exe')
const bundledEsc = join(pluginDir, 'bin', 'dsc-esc.exe')
const removedEdge = join(pluginDir, 'bin', 'dsc-edge.exe')
check('bin/dsc.exe 存在', existsSync(bundled), bundled)
check('bin/dsc-esc.exe 存在', existsSync(bundledEsc), bundledEsc)
// 光带已合进 dsc-esc，dsc-edge 应当**不存在**。
// 这条断言是反的：它防的是"删过一次又被谁加回来"。
check('bin/dsc-edge.exe 已移除', !existsSync(removedEdge), removedEdge)
if (existsSync(bundled)) {
  const kb = Math.round(statSync(bundled).size / 1024)
  check('dsc.exe 体积合理（未被打包工具破坏）', statSync(bundled).size > 20000, `${kb} KB`)
}

console.log('\n=== ② package.json 会把这些一起发布 ===')
const pkg = JSON.parse(
  (await import('node:fs')).readFileSync(join(pluginDir, 'package.json'), 'utf8'),
)
const files = pkg.files ?? []
for (const f of ['index.mjs', 'toolset.mjs', 'cordis.patch.yml', 'bin/dsc.exe', 'bin/dsc-esc.exe']) {
  check(`files 含 ${f}`, files.includes(f), files.join(','))
}
check('main 指向 index.mjs', pkg.main === 'index.mjs', String(pkg.main))
check(
  '声明了 dsh.bundle.patch',
  pkg.dsh?.bundle?.patch === './cordis.patch.yml',
  JSON.stringify(pkg.dsh),
)

console.log('\n=== ③ 插件的 YAML 不硬编码任何本机路径 ===')
const yml = (await import('node:fs')).readFileSync(join(pluginDir, 'cordis.patch.yml'), 'utf8')
check('未写死 helperPath', !/^\s*helperPath:/m.test(yml), '应留给自动解析')
check('未写死 edgePath', !/^\s*edgePath:/m.test(yml), '应留给自动解析')
check('未写死本机盘符路径', !/[A-Za-z]:\\\\/.test(yml.replace(/^\s*#.*$/gm, '')), '注释里的示例不算')
check('声明了 inject', /inject:\s*\[/.test(yml), '')
// inject 只应包含真正必需的服务；agents/workspaceRegistry/attachments 用 ctx.get() 可选获取。
// 把它们列为必需会导致没有该服务的 profile 里插件永远 pending（真实宿主实测）。
for (const svc of ['tools', 'approval']) {
  check(`inject 含 ${svc}`, new RegExp(`inject:\\s*\\[[^\\]]*"${svc}"`).test(yml), yml.match(/inject:.*/)?.[0] ?? '')
}

console.log('\n=== ④ 复现解析顺序：插件目录 → 找到自带 helper ===')
// 与 index.mjs 里的顺序一致
const resolved =
  undefined ??                                    // config.helperPath
  process.env.DSC_HELPER ??                       // 环境变量
  (existsSync(bundled) ? bundled : join(pluginDir, '..', 'helper', 'bin', 'dsc.exe'))
check('解析到插件自带的 dsc.exe', resolved === bundled, resolved)
check('解析结果确实存在', existsSync(resolved), resolved)

// 工作区判定。
//
// 这里不能简单断言"解析结果不在工作区内"——在**开发仓库**里，
// plugin/bin/ 本身就在工作区里，断言会失败，而那是正常布局。
// 真正要保证的是：**这种情况必须被检出并告警**，而不是静默失效。
//
// 分发场景（npm 装到 profile 的 node_modules）下解析结果自然在工作区之外；
// 只有"把仓库自己当 profile 装"这种开发布局才会踩到。
const inWorkspace = /[\\/]dsh workspace[\\/]/i.test(resolved)
// 这条断言原先要求 index.mjs 里出现固定的工作区目录名（正则匹配 "dsh workspace"）。
// 那个实现已经被换掉了——正因为它是**写死一台机器的目录名**，换工作区就误报
// "闸门位置不正确"，让使用者以为急停坏了。现在的实现是用 workspaceRegistry
// 反查真实工作区路径，再和 helperPath 比对。
//
// 所以断言改成检查**新的机制存在**：能反查工作区、能比对 helper 位置。
// 注意不能反过来要求"含有某个固定目录名"——那正是被修掉的错误做法，
// 而 tools/check-authpolicy.mjs 现在专门断言不能出现它。
const indexSrc = (await import('node:fs')).readFileSync(join(pluginDir, 'index.mjs'), 'utf8')
const hasWorkspaceCheck =
  /workspaceRegistry/.test(indexSrc) &&
  /helperInsideWorkspace/.test(indexSrc) &&
  /sessionIds/.test(indexSrc)

console.log(`  · 解析结果${inWorkspace ? '在' : '不在'}工作区目录内（开发仓库布局下为"在"）`)
check(
  inWorkspace ? '解析到工作区内时有检测机制（不静默失效）' : '解析结果不在工作区目录内',
  inWorkspace ? hasWorkspaceCheck : true,
  'index.mjs 应当通过 workspaceRegistry 反查工作区并比对 helper 位置',
)

console.log('\n=== ⑤ shotDir 默认值不指向仓库 ===')
const toolset = (await import('node:fs')).readFileSync(join(REPO, 'server', 'toolset.mjs'), 'utf8')
check('默认用临时目录', /tmpdir\(\),\s*'dsh-desktop-control-shots'/.test(toolset), '')
check('不再默认落仓库 .cache', !/join\(REPO_ROOT,\s*'\.cache',\s*'shots'\)/.test(toolset), '')

console.log(`\n结果：${pass} 通过，${fail} 失败`)
process.exit(fail ? 1 : 0)
