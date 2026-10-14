// 在 asar 里全局搜索 "dsh.bundle" / "bundles" 的处理位置。
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

const targets = ['dsh.bundle', 'bundle', 'bundles', 'cordis.patch']
const hits = Object.fromEntries(targets.map((t) => [t, []]))

for (const e of entries) {
  if (!/\.(js|mjs|cjs)$/.test(e.p)) continue
  const sz = e.child.size ?? 0
  if (sz > 2_000_000 || sz < 100) continue
  const t = read(e.p)
  if (!t) continue
  for (const k of targets) {
    if (t.includes(k)) hits[k].push(e.p)
  }
}

for (const k of targets) {
  console.log(`\n=== "${k}" 命中 ${hits[k].length} 个文件 ===`)
  for (const p of hits[k].slice(0, 12)) console.log('  ' + p)
}

// 重点看 profile 相关且有 bundles 的
console.log('\n\n=== 同时含 "bundles" 和 "cordis.patch" 的文件（最可能是组装器）===')
const both = hits['bundles'].filter((p) => hits['cordis.patch'].includes(p))
for (const p of both) console.log('  ★ ' + p)

for (const p of both.slice(0, 2)) {
  const t = read(p)
  const i = t.indexOf('cordis.patch')
  console.log('\n── %s 上下文 ──', p)
  console.log(t.slice(Math.max(0, i - 900), i + 900))
}
