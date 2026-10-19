#!/usr/bin/env node
/* 系统提示里的那段"该用哪个工具"：注册对了没有、够不够短、缺服务时会不会挂。
 *
 * 为什么值得单独测：
 *   · 它没有返回值、没有工具调用，出了问题**完全看不出来**（只是模型开始选错工具）；
 *   · 它必须足够短——提示是每轮都要付的固定成本；
 *   · 宿主没有 systemPrompt 服务时，注册失败绝不能让整个插件加载失败。
 *
 * 用法：node tools/test-prompt-section.mjs
 */

import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

mkdirSync(join(process.cwd(), '.cache'), { recursive: true })
const tmp = mkdtempSync(join(process.cwd(), '.cache', 'prompt-section-'))
for (const f of ['index.mjs', 'toolset.mjs', 'tooldsl.mjs', 'apps.mjs',
  'visual-candidates.mjs', 'ocr-worker.mjs', 'win-ocr.ps1']) {
  writeFileSync(join(tmp, f), readFileSync(join('plugin', f)))
}

/** 起一个插件实例，可注入一个假的 systemPrompt 服务。 */
async function boot({ withPrompt }) {
  const sections = []
  const reg = []
  const ctx = {
    logger: { info: () => {}, warn: (m) => warnings.push(String(m)), error: () => {} },
    tools: { register: (t) => { reg.push(t); return () => {} } },
    get(n) {
      if (n === 'systemPrompt' && withPrompt) {
        return { section: (sec) => { sections.push(sec); return () => {} } }
      }
      if (n === 'approval') return { request: async () => 'allowed-once' }
      if (n === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
      if (n === 'workspaceRegistry') return { list: () => [{ path: tmp, sessionIds: ['s1'] }] }
      if (n === 'attachments') return { saveImages: async () => [{ id: 'a' }] }
      return undefined
    },
  }
  const warnings = []
  const mod = await import(pathToFileURL(join(tmp, 'index.mjs')).href + '?' + Math.random())
  await mod.apply(ctx, { helperPath: join(tmp, 'no-such-helper.exe'), escPath: 'helper/bin/dsc-esc.exe', shotDir: join(tmp, 'shots') })
  return { reg, sections, warnings }
}

try {
  console.log()
  console.log('── 1. 有 systemPrompt 服务时，注册一段提示 ──')
  {
    const { reg, sections } = await boot({ withPrompt: true })
    if (sections.length === 1) ok('注册了 1 段（没有重复注册）')
    else bad(`注册了 ${sections.length} 段，期望 1 段`)

    const s = sections[0]
    if (!s) {
      bad('没有可检查的段')
    } else {
      if (s.name === 'desktop-control') ok(`段名是 ${s.name}`)
      else bad(`段名不对：${s.name}`)

      if (Number.isFinite(s.order)) ok(`order 是有限数：${s.order}`)
      else bad(`order 不合法：${s.order}`)

      const text = String(s.text ?? '')
      if (text.length > 700) bad(`提示太长（${text.length} 字符）—— 每轮都要付这个成本`)
      else ok(`长度可控：${text.length} 字符`)

      // 该说的判断规则都在（这些是工具描述里没有的"跨工具"规则）
      const wants = [
        [/click_element/, '优先按名字点'],
        [/desktop_visual_candidates/, '视觉路径入口'],
        [/\(u,v\)|u,v/, '报点约定'],
        [/ESC/, '急停语义'],
        [/重新观察|重复点击/, '不确定时先观察'],
        [/挡住|遮挡/, '截图被遮挡'],
      ]
      const missing = wants.filter(([re]) => !re.test(text)).map(([, label]) => label)
      if (missing.length === 0) ok('六条关键判断规则都在')
      else bad(`缺规则：${missing.join('、')}`)

      // 不要复述每条工具的描述（"我做什么"应该只在工具描述里说一次）
      if (!/截取一个窗口的画面|返回候选|按候选编号点击/.test(text)) {
        ok('没有复述工具描述（避免同一句话出现两次）')
      } else {
        bad('提示里复述了工具描述')
      }
    }

    if (reg.length >= 20) ok(`工具仍然注册了 ${reg.length} 个（提示不影响工具）`)
    else bad(`工具只注册了 ${reg.length} 个`)
  }

  console.log()
  console.log('── 2. 没有 systemPrompt 服务时，不能挂 ──')
  {
    let threw = null
    let out = null
    try {
      out = await boot({ withPrompt: false })
    } catch (e) {
      threw = e
    }
    if (!threw) ok('照常加载（没有因为缺服务而失败）')
    else bad(`加载失败：${threw.message}`)

    if (out && out.sections.length === 0) ok('没有注册任何段（安静降级）')
    else if (out) bad(`居然注册了 ${out.sections.length} 段`)

    if (out && out.reg.length >= 20) ok(`工具照常注册（${out.reg.length} 个）`)
    else if (out) bad(`工具没注册出来：${out.reg.length}`)
  }
} finally {
  rmSync(tmp, { recursive: true, force: true })
}

console.log()
console.log('══════════════════════════════')
console.log(`  ${pass} 项通过，${fail} 项失败`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
