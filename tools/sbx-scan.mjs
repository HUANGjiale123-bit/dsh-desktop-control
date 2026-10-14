// 在 app.asar 里搜索与"沙箱 / 注入权限"相关的配置项。
//
// 目的：确认 DSH 是否有"允许工作区内的可执行文件注入输入"这个开关。
// 如果有，就不必把 helper 挪出工作区——正当地配置它比绕过它更干净。
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

console.log('asar 内文件总数:', entries.length)

// ① 沙箱相关文件
const sbx = entries.filter(
  (e) => /sandbox/i.test(e.p) && /\.(js|mjs|cjs|json)$/.test(e.p),
)
console.log('\n=== 沙箱相关文件 (' + sbx.length + ') ===')
for (const t of sbx.slice(0, 30)) console.log('  %s  (%d bytes)', t.p, t.child.size ?? 0)

// ② 搜索与"注入 / 合成输入 / 桌面操控"相关的关键词
const KEYWORDS = [
  'allowInput', 'inputInjection', 'syntheticInput', 'SendInput',
  'allowDesktopControl', 'desktopControl', 'allowInjection',
  'allowGui', 'guiControl', 'winSta', 'windowStation',
]
const hits = []
for (const e of entries) {
  if (!/\.(js|mjs|cjs|json)$/.test(e.p)) continue
  if ((e.child.size ?? 0) > 3_000_000) continue
  const txt = read(e.p)
  if (!txt) continue
  for (const k of KEYWORDS) {
    if (txt.includes(k)) hits.push({ p: e.p, k })
  }
}
console.log('\n=== 关键词命中 (' + hits.length + ') ===')
const seen = new Set()
for (const h of hits) {
  const key = h.p + '|' + h.k
  if (seen.has(key)) continue
  seen.add(key)
  console.log('  %s  →  %s', h.k, h.p)
}

// ③ 沙箱插件的配置 schema（找 config 定义）
console.log('\n=== 沙箱插件的配置项 ===')
for (const e of sbx) {
  const txt = read(e.p)
  if (!txt) continue
  const m = txt.match(/Schema\.object\(\{[\s\S]{0,600}?\}\)/g)
  if (m) {
    console.log('── %s ──', e.p)
    for (const x of m.slice(0, 2)) console.log(x.slice(0, 500))
  }
}
