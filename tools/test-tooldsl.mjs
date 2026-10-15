// 测 tooldsl.mjs：它替代了宿主的 defineTool，所以必须自己验。
//
// 重点验三件事：
//   ① 能编译本插件实际用到的全部参数规格（不是造几个例子，是**全部**）
//   ② schema 形状与宿主期望一致（required 提层、属性节点上没有 required）
//   ③ 不支持的写法会**抛错**，而不是默默生成坏 schema

import { readFileSync } from 'node:fs'
import { compileParameters, defineTool } from '../plugin/tooldsl.mjs'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

console.log()
console.log('── 1. 编译本插件实际的全部参数规格 ──')

// 从 index.mjs 里把 parameters: {...} 块抽出来，实际编译一遍。
// 直接 import 插件不行（它依赖 ctx），所以做源码级抽取。
const src = readFileSync(new URL('../plugin/index.mjs', import.meta.url), 'utf8')

/** 从 `parameters:` 之后取出配平的 {...} 块。 */
function extractBlocks(text) {
  const out = []
  const re = /parameters:\s*\{/g
  let m
  while ((m = re.exec(text))) {
    let i = m.index + m[0].length
    let depth = 1
    let j = i
    while (j < text.length && depth > 0) {
      const c = text[j]
      if (c === '{') depth++
      else if (c === '}') depth--
      j++
    }
    out.push(text.slice(m.index + 'parameters:'.length, j))
  }
  return out
}

const blocks = extractBlocks(src)
console.log(`     从 index.mjs 抽出 ${blocks.length} 处 parameters`)

let compiled = 0
let skippedEmpty = 0
for (const b of blocks) {
  const body = b.trim()
  // 只处理字面量（含 ...HWND 这类展开的先跳过，单独处理）
  if (body === '{}') { skippedEmpty++; continue }
  try {
    // 用 Function 求值这些字面量；HWND 等常量需要提供
    const HWND = { type: 'integer', description: '窗口句柄，来自 desktop_list_windows。' }
    const BUTTON = { type: 'string', description: '鼠标键，默认 left。', enum: ['left', 'right'] }
    const fn = new Function('HWND', 'BUTTON', `return (${body})`)
    const params = fn(HWND, BUTTON)
    const schema = compileParameters(params, `tool${compiled}`)
    if (schema.type !== 'object' || typeof schema.properties !== 'object') {
      bad(`第 ${compiled + 1} 处编译结果形状不对`)
      continue
    }
    compiled++
  } catch (e) {
    bad(`第 ${compiled + 1} 处编译失败: ${e.message}`)
  }
}
if (compiled + skippedEmpty === blocks.length && fail === 0) {
  ok(`全部编译通过（${compiled} 处有参数，${skippedEmpty} 处空）`)
}

console.log()
console.log('── 2. required 提层，且不留在属性节点上 ──')

const s1 = compileParameters({ a: { type: 'string', required: true }, b: { type: 'integer' } }, 't')
if (JSON.stringify(s1.required) === '["a"]') ok('required 提到了顶层')
else bad(`required 不对: ${JSON.stringify(s1.required)}`)
if (!('required' in s1.properties.a)) ok('属性节点上没有残留 required')
else bad('属性节点上残留了 required（宿主会拒这个 schema）')

console.log()
console.log('── 3. 无参数工具 ──')

const s2 = compileParameters(undefined, 't')
if (s2.type === 'object' && Object.keys(s2.properties).length === 0) ok('无参数 → 合法空对象 schema')
else bad(`无参数编译结果不对: ${JSON.stringify(s2)}`)

console.log()
console.log('── 4. 不支持的属性必须抛错（核心：宁可失败，不要坏 schema）──')

let threw = false
try {
  compileParameters({ a: { type: 'string', default: 'x' } }, 't')
} catch { threw = true }
if (threw) ok('未知属性 default → 抛错')
else bad('未知属性被默默接受了 —— 会生成丢掉约束的 schema')

threw = false
try {
  compileParameters({ a: { description: '没有 type' } }, 't')
} catch { threw = true }
if (threw) ok('缺 type → 抛错')
else bad('缺 type 被接受了')

threw = false
try {
  compileParameters({ a: { type: 'string', enum: 'notarray' } }, 't')
} catch { threw = true }
if (threw) ok('enum 不是数组 → 抛错')
else bad('enum 非数组被接受了')

console.log()
console.log('── 5. defineTool 的整体形状 ──')

const calls = []
const tool = defineTool({
  name: 'demo',
  description: '演示',
  parameters: { x: { type: 'integer', description: 'x', required: true } },
  output: {
    schema: { type: 'object', additionalProperties: false, properties: { result: { type: 'string', required: true } } },
    render: (_a, v) => [{ type: 'text', text: v.result }],
  },
  async execute(args) { calls.push(args); return { result: 'ok' } },
  presentCall: () => ({ card: 'generic', title: 'demo', kind: 'read' }),
})

if (tool.name === 'demo' && typeof tool.execute === 'function' && typeof tool.output.render === 'function') {
  ok('返回了 name / execute / output.render')
} else bad('返回结构不对')

if (tool.parameters.type === 'object' && tool.parameters.required?.[0] === 'x') ok('parameters 是编译后的 JSON Schema')
else bad(`parameters 不对: ${JSON.stringify(tool.parameters)}`)

// 合法参数应当通过
let runOk = true
try { await tool.execute({ x: 1 }) } catch { runOk = false }
if (runOk && calls.length === 1) ok('合法参数执行成功')
else bad('合法参数被拦了')

// 缺必填应当抛 ToolArgsError
let caught = null
try { await tool.execute({}) } catch (e) { caught = e }
if (caught && caught.name === 'ToolArgsError') ok('缺必填 → ToolArgsError')
else bad(`缺必填没抛 ToolArgsError（拿到 ${caught && caught.name}）`)

// 类型错应当抛
caught = null
try { await tool.execute({ x: 'not a number' }) } catch (e) { caught = e }
if (caught && caught.name === 'ToolArgsError') ok('类型错 → ToolArgsError')
else bad('类型错没被拦')

// presentCall 遇到坏参数应返回 undefined
if (tool.presentCall({}) === undefined) ok('presentCall 坏参数 → undefined')
else bad('presentCall 坏参数没有返回 undefined')
if (tool.presentCall({ x: 1 })?.card === 'generic') ok('presentCall 好参数 → 正常返回')
else bad('presentCall 好参数不对')

console.log()
console.log('── 6. enum 生效 ──')
const tool2 = defineTool({
  name: 'demo2',
  description: 'd',
  parameters: { b: { type: 'string', enum: ['left', 'right'] } },
  output: { schema: { type: 'object', properties: { result: { type: 'string' } } }, render: () => [] },
  async execute() { return { result: '' } },
})
caught = null
try { await tool2.execute({ b: 'middle' }) } catch (e) { caught = e }
if (caught && caught.name === 'ToolArgsError') ok('enum 外的值 → ToolArgsError')
else bad('enum 没生效')
let enumOk = true
try { await tool2.execute({ b: 'left' }) } catch { enumOk = false }
if (enumOk) ok('enum 内的值 → 通过')
else bad('enum 内的值被拒了')

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
