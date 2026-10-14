// 查 DSH 的授权模型：插件工具是否有"每次调用需授权"的机制，
// 以及是否存在会话级/持久级的放行概念。
process.noAsar = true
import { readFileSync } from 'node:fs'

const ASAR = 'D:\\deepseek harness\\resources\\app.asar'
const buf = readFileSync(ASAR)
const headerSize = buf.readUInt32LE(12)
const dataOffset = 16 + headerSize
const header = JSON.parse(buf.toString('utf8', 16, dataOffset))

const entries = []
function walk(node, path) {
  for (const [name, child] of Object.entries(node.files ?? {})) {
    const p = `${path}/${name}`
    if (child.files) walk(child, p)
    else entries.push({ p, child })
  }
}
walk(header, '')

function read(p) {
  const e = entries.find((x) => x.p === p)
  if (!e) return null
  const off = Number(e.child.offset)
  return buf.toString('utf8', dataOffset + off, dataOffset + off + (e.child.size ?? 0))
}

// 找授权插件的入口
const auth = entries.filter((e) => /dsh-authorization/.test(e.p) && /\.(js|mjs|cjs)$/.test(e.p))
console.log('=== 授权插件文件 ===')
for (const a of auth) console.log('  %s (%d bytes)', a.p, a.child.size ?? 0)

// 搜关键词
const KW = [
  'approval', 'approve', 'prompt', 'confirm', 'session-scoped', 'sessionScoped',
  'alwaysAllow', 'allowAlways', 'persist', 'remember', 'whitelist', 'allowlist',
  'permission', 'authorize',
]
console.log('\n=== 授权模型关键词命中 ===')
const seen = new Set()
for (const e of entries) {
  if (!/dsh-authorization|dsh-plugin-manager|dsh-tools/.test(e.p)) continue
  if (!/\.(js|mjs|cjs)$/.test(e.p)) continue
  if ((e.child.size ?? 0) > 2_000_000) continue
  const txt = read(e.p)
  if (!txt) continue
  for (const k of KW) {
    const i = txt.indexOf(k)
    if (i >= 0 && !seen.has(k)) {
      seen.add(k)
      console.log('  %-16s in %s', k, e.p)
    }
  }
}

// 看授权插件的 README / package.json 描述
console.log('\n=== 授权插件描述 ===')
for (const e of entries) {
  if (!/dsh-authorization.*package\.json$/.test(e.p)) continue
  console.log(read(e.p))
}
