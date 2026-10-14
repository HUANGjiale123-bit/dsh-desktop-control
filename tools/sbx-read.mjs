// 读 DSH 的 Windows ACL 沙箱实现，确认它管的是文件还是别的。
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

for (const p of [
  '/dsh/node_modules/@deepseek-ai/dsh-sandbox-windows-acl/lib/index.js',
  '/dsh/node_modules/@deepseek-ai/dsh-sandbox-windows-acl/lib/runner.js',
  '/dsh/node_modules/@deepseek-ai/dsh-sandbox-windows-acl/package.json',
  '/dsh/node_modules/@deepseek-ai/dsh-sandbox-policy/lib/index.js',
]) {
  const t = read(p)
  console.log('\n' + '='.repeat(70))
  console.log(p)
  console.log('='.repeat(70))
  console.log(t ? t.slice(0, 3000) : '(读不到)')
}
