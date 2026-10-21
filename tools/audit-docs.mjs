#!/usr/bin/env node
/* 检查"对外文档里写的每一句"能不能在代码里找到依据。
 *
 * ── 为什么需要这个 ──
 *
 * 安装说明连着三版出错：编了一个不存在的命令、把我自己的临时测试目录当成人家的路径、
 * 漏掉"手动装"的第一步（只登记 bundle 不装包）、还声称急停会"60 秒自动解除"。
 * 这些都不是打字错误——是**凭印象写文档**。人写文档时会回忆，而回忆会编。
 *
 * 所以把能机械核对的东西全部核对掉：
 *   ① 文档里提到的每个文件路径 → 必须存在
 *   ② 文档里提到的每个工具名 → 必须真的注册过
 *   ③ 文档里提到的每个配置键 → 必须真的被代码读取
 *   ④ 文档里提到的版本号 → 必须等于当前版本
 *   ⑤ 文档里出现的 `dsc.exe <子命令>` → 必须是 helper 真实支持的子命令
 *
 * 用法：node tools/audit-docs.mjs [文件...]
 * 默认检查 dist/ 下所有 .md 与 docs/对外汇报-*.md
 */

import { readFileSync, readdirSync, existsSync } from 'node:fs'
import { join, basename, sep } from 'node:path'

let problems = 0
const report = (file, line, what) => {
  console.log(`  ✗ ${basename(file)}:${line}  ${what}`)
  problems++
}

// ── 事实来源 ──
const index = readFileSync('plugin/index.mjs', 'utf8')
const helper = readFileSync('helper/src/Program.cs', 'utf8')
const pkg = JSON.parse(readFileSync('plugin/package.json', 'utf8'))
const version = pkg.version

const tools = new Set([...index.matchAll(/name: '(desktop_[a-z_]+)'/g)].map((m) => m[1]))
const configKeys = new Set([...index.matchAll(/config\.([a-zA-Z]+)/g)].map((m) => m[1]))
const helperCommands = new Set([...helper.matchAll(/case "([a-z_]+)":/g)].map((m) => m[1]))

const files = process.argv.slice(2).length
  ? process.argv.slice(2)
  : [
    ...readdirSync('dist').filter((f) => f.endsWith('.md')).map((f) => join('dist', f)),
    ...readdirSync('docs').filter((f) => /^对外汇报-.*\.md$/.test(f)).map((f) => join('docs', f)),
  ]

console.log(`检查 ${files.length} 个文档（代码事实：${tools.size} 个工具、${configKeys.size} 个配置键、${helperCommands.size} 个子命令、版本 ${version}）`)
console.log()

for (const file of files) {
  if (!existsSync(file)) { console.log(`  · 跳过不存在的文件 ${file}`); continue }
  const lines = readFileSync(file, 'utf8').split('\n')

  lines.forEach((text, i) => {
    const n = i + 1

    // ① 工具名：desktop_xxx
    for (const m of text.matchAll(/\b(desktop_[a-z_]+)\b/g)) {
      if (!tools.has(m[1])) report(file, n, `工具 "${m[1]}" 在代码里不存在`)
    }

    // ② 配置键：`cursorFx: true` 这类写法
    for (const m of text.matchAll(/`([a-zA-Z]+):\s*(true|false|[0-9"\[])/g)) {
      const key = m[1]
      // 只检查看起来像插件配置的键（出现在我们认识的集合里，或者是已知的伪键）
      if (/^(cursor|allowed|shot|helper|esc|gate|ocr|idle)/i.test(key) && !configKeys.has(key)) {
        report(file, n, `配置键 "${key}" 代码里没有读取`)
      }
    }

    // ③ 版本号：只在**对外发放的 dist/ 文档**里强制等于当前版本。
    //    历史评审文档引用旧版本号（"上一份是 2.18.51"）是正常的，不能报错。
    if (file.split(sep)[0] === 'dist') {
      for (const m of text.matchAll(/\b(2\.18\.\d+)\b/g)) {
        if (m[1] !== version) report(file, n, `版本号 ${m[1]} ≠ 当前 ${version}`)
      }
    }

    // ④ dsc.exe 子命令
    for (const m of text.matchAll(/dsc\.exe\s+([a-z_]+)/g)) {
      if (!helperCommands.has(m[1])) report(file, n, `子命令 "${m[1]}" helper 不支持`)
    }

    // ⑤ 明确引用的文件路径（带扩展名、看起来是相对仓库的）
    for (const m of text.matchAll(/\b((?:plugin|helper|dist|docs|tools)\/[A-Za-z0-9_.\-\u4e00-\u9fa5/]+\.(?:mjs|exe|png|json|yml|md|cs|ps1|py))\b/g)) {
      if (!existsSync(m[1])) report(file, n, `引用的文件 "${m[1]}" 不存在`)
    }
  })
}

// ── 许可证必须真实存在且与元数据一致 ──
//
// 踩过：package.json 写 "SEE LICENSE IN LICENSE"，但仓库里根本没有 LICENSE。
// 这类"元数据指向不存在的文件"无声无息，直到发布被拦。
{
  const pkg = JSON.parse(readFileSync('plugin/package.json', 'utf8'))
  if (pkg.license === 'MIT') console.log('  ✓ package.json license = MIT')
  else report('plugin/package.json', 0, `license 是 ${JSON.stringify(pkg.license)}，期望 MIT`)

  for (const p of ['LICENSE', 'plugin/LICENSE']) {
    if (!existsSync(p)) { report(p, 0, 'LICENSE 不存在'); continue }
    const text = readFileSync(p, 'utf8')
    if (/^MIT License/m.test(text) && /Copyright \(c\) \d{4} /.test(text)) {
      console.log(`  ✓ ${p} 是 MIT 全文（含版权行）`)
    } else {
      report(p, 0, '不是完整的 MIT 文本或缺少版权行')
    }
  }
}

// ── dist/ 文件名里的版本号也必须等于当前版本 ──
//
// 踩过：版本升到 1.0.2，正文都改了，文件名还挂着 "-1.0.0"，
// 发出去的包里并排躺着两代文档，收的人不知道哪份是新的。
{
  for (const f of readdirSync('dist')) {
    const m = f.match(/-(\d+\.\d+\.\d+)/)
    if (m && m[1] !== version) {
      console.log(`  ✗ dist/${f}  文件名里的版本 ${m[1]} ≠ 当前 ${version}`)
      problems++
    }
  }
}

// ── dist/ 里的副本必须与源文件一致 ──
//
// 踩过：改完 tools/smoke-report.mjs 忘了同步 dist/，发出去的还是旧脚本，
// 第三方复核指出"和上一版字节完全相同"。这类"发错版本"无声音、后果直接。
{
  const mirrors = [
    ['tools/smoke-report.mjs', 'dist/smoke-report.mjs'],
    ['docs/第二台机器验收清单.md', 'dist/第二台机器验收清单.md'],
  ]
  for (const [src, dst] of mirrors) {
    if (!existsSync(src) || !existsSync(dst)) continue
    const a = readFileSync(src, 'utf8'), b = readFileSync(dst, 'utf8')
    if (a === b) console.log(`  ✓ ${dst} 与源文件一致`)
    else report(dst, 0, `与 ${src} 不一致（发出去的是旧副本）`)
  }
}

console.log()
if (problems === 0) {
  console.log('  ✓ 没有发现文档与代码不一致的地方')
} else {
  console.log(`  ✗ ${problems} 处需要修`)
}
process.exit(problems === 0 ? 0 : 1)
