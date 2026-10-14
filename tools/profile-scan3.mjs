// 读 profile-boot 与 app-boot 里关于 bundle → patch 的解析逻辑。
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
  '/dsh/node_modules/@deepseek-ai/dsh/lib/profile-boot-BZ2ZjNWi.js',
  '/dsh/node_modules/@deepseek-ai/dsh-app-boot/lib/index.js',
]) {
  const t = read(p)
  if (!t) { console.log(p, '(读不到)'); continue }
  console.log('\n' + '='.repeat(72))
  console.log(p, `(${t.length} bytes)`)
  console.log('='.repeat(72))

  // 打印含 patch / bundle 的片段
  const lines = t.split('\n')
  let shown = 0
  for (let i = 0; i < lines.length && shown < 40; i++) {
    const l = lines[i]
    if (/dsh\.bundle|cordis\.patch|bundlePatch|patchFile|bundles/.test(l)) {
      console.log(`${String(i + 1).padStart(4)}: ${l.slice(0, 180)}`)
      shown++
    }
  }
}
