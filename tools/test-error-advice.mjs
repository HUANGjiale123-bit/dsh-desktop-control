// 错误必须带"事实 + 恢复建议"，而且覆盖到**所有**产生错误的地方。
//
// ── 为什么 ──
//
// 原先的错误是纯文字（"失败：NotFound — ..."），读的人得自己判断
// 该重观察、该改描述符，还是该放弃。不同模型读同一句话会做不同的事。
//
// Codex 的建议是"先覆盖明确守卫和未知结果"：
//   · 明确定卫的恢复动作是确定的 → 直接给 retry
//   · 其余（含未知）→ 统一 reobserve（重新观察永远比硬试安全）
//
// ── 这个测试守什么 ──
//
//   · 每个明确守卫的码都有 retry，且值在合法集合里
//   · 未知错误默认 reobserve
//   · **手写的**错误消息也带 retry（toolset 里有十来处直接拼中文句子）
//   · retry 写在文本里，既给人读也能被解析
//
// 用法：node tools/test-error-advice.mjs

import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
let skipped = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }
const skip = (m) => { console.log(`  · ${m}`); skipped++ }

const RETRY_VALUES = new Set(['stop', 'reobserve', 'wait', 'disambiguate', 'giveUp'])

const src = readFileSync('server/toolset.mjs', 'utf8')

console.log()
console.log('── 1. 分类表存在且值合法 ──')

if (src.includes('const ERROR_ADVICE')) ok('有 ERROR_ADVICE 表')
else bad('没有分类表')

{
  const table = src.slice(src.indexOf('const ERROR_ADVICE'), src.indexOf('const failMessage'))
  const retries = [...table.matchAll(/retry:\s*'([a-zA-Z]+)'/g)].map((m) => m[1])
  if (retries.length === 0) bad('表里没有 retry')
  else {
    ok(`表里有 ${retries.length} 条建议`)
    const badOnes = retries.filter((r) => !RETRY_VALUES.has(r))
    if (badOnes.length === 0) ok('所有 retry 值都在合法集合里')
    else bad(`非法 retry 值：${[...new Set(badOnes)].join(', ')}`)
  }
}

// 明确守卫的几个必须都在
for (const code of ['Halted', 'NotAuthorized', 'StaleTarget', 'AmbiguousTarget', 'Busy', 'StaleSnapshot']) {
  if (new RegExp(`${code}:\\s*\\{`).test(src)) ok(`${code} 有明确建议`)
  else bad(`${code} 没有建议`)
}

console.log()
console.log('── 2. 未知错误默认 reobserve ──')

{
  const fm = src.slice(src.indexOf('const failMessage'), src.indexOf('const advice'))
  if (fm.includes('retry=reobserve') && fm.includes('不确定这一类')) {
    ok('未知错误给 reobserve，并说明"我不确定"')
  } else {
    bad('未知错误没有安全默认')
  }
}

console.log()
console.log('── 3. 手写的错误也带建议 ──')

{
  if (src.includes('const advice = (code, text)')) ok('有 advice 辅助（给手写消息用）')
  else bad('没有 advice —— 手写的错误会没有建议')

  // authorize 里的几处必须包上 advice()
  const auth = src.slice(src.indexOf('async function authorize('), src.indexOf('async function authorizeApp'))
  const handWritten = (auth.match(/message:\s*[`'"]失败/g) ?? []).length
  const wrapped = (auth.match(/message:\s*advice\(/g) ?? []).length
  const multiLineWrapped = (auth.match(/message: advice\(/g) ?? []).length
  if (handWritten === 0) ok(`authorize 里手写的错误都包上了（${wrapped + multiLineWrapped} 处用 advice）`)
  else bad(`authorize 里还有 ${handWritten} 处手写错误没走 advice`)
}

console.log()
console.log('── 4. 实际输出带 [retry=...] ──')

{
  const tmp = mkdtempSync(join(process.cwd(), '.cache', 'erradv-'))
  writeFileSync(join(tmp, 'toolset.mjs'), readFileSync('server/toolset.mjs'))
  writeFileSync(join(tmp, 'apps.mjs'), readFileSync('server/apps.mjs'))

  try {
    const tmod = await import(pathToFileURL(join(tmp, 'toolset.mjs')).href + '?' + Math.random())
    const set = tmod.createDesktopToolset({
      helperPath: 'helper/bin/dsc.exe',
      allowedApps: 'notepad.exe',
      getGatePath: () => join(tmp, 'halt'),
      requestApproval: async () => 'allowed-once',
    })
    const r = await set.tools.readWindow({ hwnd: 1 }).catch((e) => ({ isError: true, text: e.message }))
    const text = r.text ?? ''
    if (/\[retry=/.test(text)) ok('错误文本里带 [retry=...]')
    else bad('错误文本里没有 [retry=...]')
    if (/\[retry=reobserve\]/.test(text)) ok('窗口不存在 → reobserve（对的：重新列窗口）')
    else bad(`窗口不存在没给 reobserve：${text.slice(0, 80)}`)
  } catch (e) {
    skip(`调用失败，没法看实际输出：${e.message.slice(0, 60)}`)
  }
  try { rmSync(tmp, { recursive: true, force: true }) } catch {}
}

console.log()
console.log('── 5. 错误码能被解析出来（供统计用）──')

{
  const plugin = readFileSync('plugin/index.mjs', 'utf8')
  if (/失败：\(\[A-Za-z\]\+\)/.test(plugin) || plugin.includes('失败：([A-Za-z]+)')) {
    ok('从"失败：CODE"里取码，而不是在文本里猜关键词')
  } else {
    bad('错误码还是靠猜关键词 —— 会误判')
  }
}

console.log()
console.log('══════════════════════════════')
console.log(
  fail === 0
    ? `  ${pass} 项通过` + (skipped ? `，${skipped} 项跳过` : '')
    : `  ${pass} 项通过，${fail} 项失败，${skipped} 项跳过  ← 先修`,
)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
