// 工具定义的本地实现 —— 替代 `@deepseek-ai/dsh-tools` 的 `defineTool`。
//
// ── 为什么要自己实现 ──
//
// 插件原本 `import { defineTool } from '@deepseek-ai/dsh-tools'`（顶层的裸 import）。
// 这个包不在 profile 的 node_modules 里，而是由宿主的 `bareModuleBaseUrl`
// 解析到宿主安装位置。这样一来：
//
//   · 宿主能解析 → 插件正常
//   · 宿主解析不到 → **模块加载直接失败**，而且失败发生在模块顶层，
//     连插件自己的 try/catch 兜底都进不去，只留下"所有工具都不存在"
//
// 实测：朋友机器上就是后者，而本机是前者。插件作者无法控制宿主的解析行为，
// 所以**不依赖它**才是稳的。参数规格到 JSON Schema 的转换就十几行，
// 自己写掉，这个依赖也就没有了。
//
// ── 为什么不照抄真正的实现 ──
//
// 真实现有一套 author-facing 规格编译器（`compilePropertyMap` /
// `runSchemaCompiler`），支持相当多的写法。本插件只用得到四个属性：
// type / description / required / enum。照抄那套编译器既没必要，
// 也会引入我无法完整测试的复杂度。
//
// 所以这里写一个**只支持这四个属性、遇到其他属性就抛错**的版本。
// 关键在"抛错"：如果哪天给参数加了新属性而这里不认识，
// 我希望它在加载时就响亮地失败，而不是**默默生成一个丢掉该约束的 schema**
// ——后者会让模型收到错误的参数契约，且完全没有征兆。

/** 允许出现在参数规格里的属性。不在这个集合里的一律报错。 */
const ALLOWED_SPEC_KEYS = new Set(['type', 'description', 'required', 'enum'])

/** 允许出现在输出 schema 里的顶层键。 */
const ALLOWED_OUTPUT_KEYS = new Set(['type', 'properties', 'required', 'additionalProperties', 'items', 'enum'])

function schemaError(message) {
  throw new Error(`defineTool: ${message}`)
}

/**
 * 把一个参数规格（author-facing）转成 JSON Schema 节点。
 *
 * 与真实现一致的两个要点：
 *   · `required: true` 是**属性上的标注**，不是 JSON Schema 的一部分；
 *     它要被提到父级的 `required` 数组里，并且不能留在属性节点上
 *     （宿主会校验 schema 子集，多余的键会被拒）。
 *   · `enum` 原样保留。
 */
function specToSchema(spec, path) {
  if (spec === null || typeof spec !== 'object' || Array.isArray(spec)) {
    schemaError(`${path} 必须是一个对象，收到 ${JSON.stringify(spec)}`)
  }
  for (const k of Object.keys(spec)) {
    if (!ALLOWED_SPEC_KEYS.has(k)) {
      schemaError(
        `${path} 含不支持的属性 "${k}"。` +
          `支持的属性：${[...ALLOWED_SPEC_KEYS].join(', ')}。` +
          `要新增属性的话得同时扩展本文件的转换逻辑——` +
          `宁可在这里失败，也不要生成一个丢掉了约束的 schema。`,
      )
    }
  }
  if (typeof spec.type !== 'string') schemaError(`${path} 缺少 type`)

  const out = { type: spec.type }
  if (spec.description !== undefined) out.description = String(spec.description)
  if (spec.enum !== undefined) {
    if (!Array.isArray(spec.enum)) schemaError(`${path}.enum 必须是数组`)
    out.enum = spec.enum.slice()
  }
  return out
}

/**
 * 把 `{ 参数名: 规格 }` 编译成 `{ type:'object', properties, required? }`。
 *
 * 无参数时返回一个合法的空对象 schema —— 宿主要求 `parameters` 必须存在
 * 且可编译，早先返回 `undefined` 会让注册失败。
 */
export function compileParameters(parameters, toolName) {
  const where = `${toolName}.parameters`
  if (parameters === undefined || parameters === null) {
    return { type: 'object', properties: {} }
  }
  if (typeof parameters !== 'object' || Array.isArray(parameters)) {
    schemaError(`${where} 必须是对象`)
  }

  const properties = {}
  const required = []
  for (const [name, spec] of Object.entries(parameters)) {
    properties[name] = specToSchema(spec, `${where}.${name}`)
    if (spec && spec.required === true) required.push(name)
  }

  const schema = { type: 'object', properties }
  if (required.length > 0) schema.required = required
  return schema
}

/** 校验输出 schema 的形状（与宿主用到的键保持一致）。 */
function assertOutputSchema(schema, toolName) {
  const where = `${toolName}.output.schema`
  if (schema === null || typeof schema !== 'object' || Array.isArray(schema)) {
    schemaError(`${where} 必须是对象`)
  }
  for (const k of Object.keys(schema)) {
    if (!ALLOWED_OUTPUT_KEYS.has(k)) {
      schemaError(
        `${where} 含不支持的键 "${k}"。支持的键：${[...ALLOWED_OUTPUT_KEYS].join(', ')}。`,
      )
    }
  }
  if (typeof schema.type !== 'string') schemaError(`${where} 缺少 type`)
  if (schema.type === 'object' && schema.required !== undefined) {
    if (!Array.isArray(schema.required)) schemaError(`${where}.required 必须是数组`)
    for (const r of schema.required) {
      if (typeof r !== 'string') schemaError(`${where}.required 只能含字符串`)
      if (!schema.properties || !Object.hasOwn(schema.properties, r)) {
        schemaError(`${where}.required 里的 "${r}" 不在 properties 里`)
      }
    }
  }
}

/**
 * 按参数 schema 做最小校验：必填存在、类型对得上。
 *
 * **刻意不做完整 JSON Schema 校验。** 真正的实现会走一遍 schema walker，
 * 我这里只挡最常见也最要紧的两类错误：缺必填、类型错。理由是参数由模型生成，
 * 而工具自己的实现会把不合法的值变成明确的错误消息返回——
 * 在这里过度校验只会把"参数不对"变成更难懂的异常。
 */
function validateArgs(schema, args) {
  const violations = []
  if (args === null || typeof args !== 'object' || Array.isArray(args)) {
    return [`参数必须是对象，收到 ${Array.isArray(args) ? 'array' : typeof args}`]
  }
  for (const r of schema.required ?? []) {
    if (args[r] === undefined || args[r] === null) violations.push(`缺少必填参数 "${r}"`)
  }
  for (const [name, spec] of Object.entries(schema.properties ?? {})) {
    const v = args[name]
    if (v === undefined || v === null) continue
    const t = spec.type
    const ok =
      t === 'string' ? typeof v === 'string'
      : t === 'integer' ? Number.isInteger(v)
      : t === 'number' ? typeof v === 'number' && Number.isFinite(v)
      : t === 'boolean' ? typeof v === 'boolean'
      : t === 'array' ? Array.isArray(v)
      : t === 'object' ? typeof v === 'object' && !Array.isArray(v)
      : true
    if (!ok) violations.push(`参数 "${name}" 应为 ${t}，收到 ${Array.isArray(v) ? 'array' : typeof v}`)
    if (spec.enum && !spec.enum.includes(v)) {
      violations.push(`参数 "${name}" 必须是 ${spec.enum.map((x) => JSON.stringify(x)).join(' / ')} 之一`)
    }
  }
  return violations
}

/**
 * 定义一个工具。签名与 `@deepseek-ai/dsh-tools` 的 `defineTool` 一致，
 * 覆盖本插件用到的部分。
 *
 * 与真实现的一处**有意差异**：这里不做输出值的 schema 校验。
 * 输出可控（全是插件自己拼的字符串），而真实现那套输出校验需要完整的
 * schema walker。少这一层不会让错误溜出去——`output.render` 拿到什么就渲染什么。
 */
export function defineTool(options) {
  const name = options?.name
  if (typeof name !== 'string' || name === '') schemaError('缺少 name')
  if (typeof options.description !== 'string') schemaError(`${name}: 缺少 description`)
  if (!options.output || typeof options.output.render !== 'function') {
    schemaError(`${name}: output.render 必须是函数`)
  }
  assertOutputSchema(options.output.schema, name)
  if (typeof options.execute !== 'function') schemaError(`${name}: execute 必须是函数`)

  const parameters = compileParameters(options.parameters, name)
  const userExecute = options.execute
  const userPresentCall = options.presentCall

  const tool = {
    name,
    description: options.description,
    parameters,
    output: {
      schema: options.output.schema,
      render: (args, value) => options.output.render(args, value),
    },
    async execute(args, exec) {
      const violations = validateArgs(parameters, args)
      if (violations.length > 0) {
        const err = new Error(`工具 ${name} 的参数不合法：${violations.join('；')}`)
        err.name = 'ToolArgsError'
        throw err
      }
      return userExecute(args, exec)
    },
  }

  // presentCall 拿到的是模型生成的参数，坏了就不该展示。
  // 真实现的行为是返回 undefined（退回通用渲染），这里保持一致。
  if (typeof userPresentCall === 'function') {
    tool.presentCall = (args) => {
      if (validateArgs(parameters, args).length > 0) return undefined
      return userPresentCall(args)
    }
  }

  return tool
}
