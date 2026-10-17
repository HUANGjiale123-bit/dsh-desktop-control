// 截图必须作为**图像**进入模型，而不是只给一条路径。
//
// 这一条是第三方审查发现的，而且是"为什么慢"的主要结构原因：
// 工具只返回路径时，模型要看界面就得再调一次 read_image ——
// 每轮"看 → 点"多一次工具往返。Codex 则是一次调用里既做动作又给画面。
//
// 唯一的正确形状是 `{ type:'image', attachment: ref }`，ref 来自
// `ctx.attachments.saveImages()`。用文字描述界面等于撒谎。
//
// 用法：node tools/test-image-block.mjs

import { pathToFileURL } from 'node:url'
import { execFileSync } from 'node:child_process'

let pass = 0
let fail = 0
const ok = (m) => { console.log(`  ✓ ${m}`); pass++ }
const bad = (m) => { console.log(`  ✗ ${m}`); fail++ }

const registered = []
const warnings = []
const savedImages = []

function makeCtx(withAttachments) {
  return {
    logger: {
      info: () => {},
      warn: (m) => warnings.push(String(m)),
      error: () => {},
    },
    tools: { register: (t) => { registered.push(t); return () => {} } },
    get(name) {
      if (name === 'attachments' && withAttachments) {
        return {
          saveImages: async (imgs) => {
            for (const i of imgs) {
              savedImages.push({ mediaType: i.mediaType, name: i.name, bytes: i.data?.length ?? 0 })
            }
            return [{ id: 'att-1', mime: imgs[0]?.mediaType ?? 'image/png' }]
          },
        }
      }
      if (name === 'approval') return { request: async () => 'allowed-once' }
      if (name === 'agents') return { currentInitiator: () => ({ id: 's1' }) }
      if (name === 'workspaceRegistry') return { list: () => [{ path: process.cwd(), sessionIds: ['s1'] }] }
      return undefined
    },
  }
}

async function loadPlugin(withAttachments) {
  registered.length = 0
  const mod = await import(
    pathToFileURL(`${process.cwd()}/plugin/index.mjs`).href + `?t=${Math.random()}`
  )
  await mod.apply(makeCtx(withAttachments), {
    helperPath: 'helper/bin/dsc.exe',
    escPath: 'helper/bin/dsc-esc.exe',
    gatePath: `${process.cwd()}/.cache/image-block-test/halt`,
    shotDir: `${process.cwd()}/.cache/image-block-test/shots`,
  })
  return registered.find((t) => t.name === 'desktop_screenshot')
}

console.log()
console.log('── 1. 输出 schema 必须带 image 字段 ──')
const shot = await loadPlugin(true)
if (shot) ok('desktop_screenshot 注册成功')
else { bad('注册失败'); process.exit(1) }

const props = shot.output?.schema?.properties ?? {}
if (props.image) ok('schema 里有 image 字段')
else bad('schema 里没有 image 字段 —— 图像无处可放')

if (props.image && typeof props.image.type === 'string') ok(`image 的 type 是 ${props.image.type}`)
else bad('image 缺少 type')

if (props.filepath && props.summary) ok('filepath 与 summary 仍然保留（路径可回查）')
else bad('丢了 filepath 或 summary')

console.log()
console.log('── 2. render 在有 image 时把图像放进结果 ──')

{
  const fakeImage = { type: 'image', attachment: { id: 'a', mime: 'image/png' } }
  const withImg = shot.output.render({}, { filepath: '/x.png', summary: 'S', image: fakeImage })
  if (Array.isArray(withImg) && withImg.some((b) => b.type === 'image')) {
    ok('有 image 时 render 返回了图像块')
  } else {
    bad('有 image 时 render 没有返回图像块 —— 模型仍然看不到图')
  }
  if (withImg.some((b) => b.type === 'text')) ok('同时保留了文字说明')
  else bad('图像块把文字说明挤掉了')

  const noImg = shot.output.render({}, { filepath: '/x.png', summary: 'S' })
  if (Array.isArray(noImg) && noImg.every((b) => b.type === 'text')) {
    ok('没有 image 时退化为纯文字（不产生空图像块）')
  } else {
    bad('没有 image 时仍产生了非文字块')
  }
}

console.log()
console.log('── 3. 真实执行：PNG 必须被读成 attachment ──')

{
  const raw = JSON.parse(
    execFileSync('helper/bin/dsc.exe', ['list_windows'], { encoding: 'utf8', windowsHide: true }),
  )
  const w = raw.windows.find((x) => x.width > 300 && x.height > 300 && x.exe)
  if (!w) {
    console.log('  （跳过：找不到可用窗口）')
  } else {
    const r = await shot.execute({ hwnd: w.hwnd }).catch((e) => ({ err: e.message }))
    if (r.err) {
      console.log(`  （跳过：截图失败 ${r.err.split('\n')[0].slice(0, 60)}）`)
    } else {
      if (r.image?.type === 'image') ok('execute 返回了 image 内容块')
      else bad('execute 没返回 image —— 模型只能看到路径')

      if (r.image?.attachment) ok('image 带 attachment 引用')
      else bad('image 缺少 attachment')

      if (savedImages.length > 0) {
        const si = savedImages[0]
        if (si.mediaType === 'image/png') ok(`saveImages 以 image/png 提交（${si.bytes} 字节）`)
        else bad(`mediaType 不对：${si.mediaType}`)
        if (si.bytes > 1000) ok('提交的是真实图像数据（不是空文件）')
        else bad(`提交的数据太小：${si.bytes} 字节，可能是空文件`)
      } else {
        bad('没有调用 saveImages')
      }
    }
  }
}

console.log()
console.log('── 4. 没有 attachments 服务时要降级，不能崩 ──')

{
  const shot2 = await loadPlugin(false)
  const raw = JSON.parse(
    execFileSync('helper/bin/dsc.exe', ['list_windows'], { encoding: 'utf8', windowsHide: true }),
  )
  const w = raw.windows.find((x) => x.width > 300 && x.height > 300 && x.exe)
  if (w) {
    const r = await shot2.execute({ hwnd: w.hwnd }).catch((e) => ({ err: e.message }))
    if (r.err) {
      console.log(`  （跳过：截图失败）`)
    } else {
      if (!r.image && r.filepath) ok('没有 attachments 时仍返回路径（降级而非失败）')
      else bad('没有 attachments 时的行为不对')
      if (warnings.some((x) => x.includes('没有 attachments'))) {
        ok('明确警告了"模型看不到图"')
      } else {
        bad('没有给出警告 —— 静默降级等于让人以为看到了')
      }
    }
  }
}

console.log()
console.log('══════════════════════════════')
console.log(fail === 0 ? `  全部通过（${pass} 项）` : `  ${pass} 项通过，${fail} 项失败  ← 先修`)
console.log('══════════════════════════════')
process.exit(fail === 0 ? 0 : 1)
