// 查 DSH 的 profile 组装逻辑：bundle 自带的 patch 是否会生效。
//
// 这决定了插件能否"装完即用"：
//   · 如果 bundle 内部的 cordis.patch.yml 会被应用 → 安装即生效，用户什么都不用改
//   · 如果不会 → 插件的 inject 声明永远不被读到，装完必然是坏的
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

// 找 profile / bundle 组装相关的实现
console.log('=== 含 profile.bundles 处理逻辑的文件 ===')
const cand = entries.filter(
  (e) => /\.(js|mjs|cjs)$/.test(e.p) && /dsh-profile|loader|bundle/i.test(e.p),
)
for (const c of cand.slice(0, 25)) {
  const t = read(c.p)
  if (t && /bundles/.test(t) && /patch/.test(t)) {
    console.log('  ★ %s (%d bytes)', c.p, c.child.size ?? 0)
  } else if (t && /bundles/.test(t)) {
    console.log('    %s (%d bytes)', c.p, c.child.size ?? 0)
  }
}

// 在候选文件里搜 bundle 内部 patch 的解析
console.log('\n=== 搜 "bundle 内部 patch" 的解析线索 ===')
const KW = ['dsh.bundle', 'bundle.patch', 'patchFile', 'patchPath', 'resolveBundle', 'bundlePatch']
for (const c of cand) {
  const t = read(c.p)
  if (!t) continue
  for (const k of KW) {
    if (t.includes(k)) {
      const i = t.indexOf(k)
      console.log('\n── %s ── 关键词 %s', c.p, k)
      console.log(t.slice(Math.max(0, i - 260), i + 420))
      break
    }
  }
}
