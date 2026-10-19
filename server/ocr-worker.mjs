/**
 * OCR 后端：**调用本机自带的识别能力，不打包任何模型**。
 *
 * ── 为什么是这个方案 ──
 *
 * 备选是 RapidOCR + onnxruntime：质量更好，但要把几十 MB 的依赖和模型塞进
 * 给朋友的 tgz，还要锁版本、断网可用、解压后能跑。而 Windows 10/11 自带
 * `Windows.Media.Ocr`（跟着系统语言包走）——
 *
 *   实测（本机，688x1055 中文界面截图）：OCR 本体 104ms，无需安装任何东西。
 *
 * 所以策略是「**用别人自己机器上的 OCR**」：探测它有什么语言包，
 * 有什么就用什么；没有就返回 VisualBackendUnavailable，插件其余功能照常。
 * 以后如果要更好的质量，再加一个可选后端（RapidOCR 等），接口不变。
 *
 * ── 边界 ──
 *
 * · 只读**已经登记过的截图文件**，不接受任意 URL / 任意路径（由调用方查注册表）。
 * · 不点击、不碰鼠标键盘；它不是输入通道。
 * · 不解读结果：只把「文字 + 位置」交出去，"这是不是一个按钮"由上层判断。
 * · OCR 分数只是**文字识别分数**，不是"可点击置信度"，所以这里不改名。
 */

import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { pngSize } from './visual-candidates.mjs'

/* ── 两个**测试钩子**（只影响本模块，不影响任何生产默认值）──
 *
 *   DSC_POWERSHELL   换成另一个可执行文件，用来验"解释器本身不可用"这条路；
 *   DSC_OCR_SCRIPT   换成另一个脚本，用来验"协议不符/输出是垃圾/卡死超时"。
 *
 * 为什么要有：第 7 条验收要求"worker 缺依赖 / 管道断 / 协议不符 / 父进程结束
 * → 有界失败，不拖垮插件"。这些状态在正常机器上根本复现不出来，
 * 而"没法复现"就等于"没验证"。留两个环境变量，就能把失败面真跑一遍。
 */
const PS = process.env.DSC_POWERSHELL ||
  'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe'

/** 脚本位置：插件打包后与 worker 同目录；仓库里在 server/ 下。 */
function scriptPath() {
  if (process.env.DSC_OCR_SCRIPT) return process.env.DSC_OCR_SCRIPT
  const here = dirname(fileURLToPath(import.meta.url))
  const cand = [join(here, 'win-ocr.ps1'), join(here, '..', 'server', 'win-ocr.ps1')]
  for (const c of cand) if (existsSync(c)) return c
  return null
}

function decode(out) {
  const line = String(out ?? '').trim().split('\n').filter(Boolean).pop()
  if (!line) return { ok: false, error: 'NoOutput' }
  try {
    return JSON.parse(line)
  } catch (e) {
    return { ok: false, error: `BadJson: ${e.message}`, raw: line.slice(0, 300) }
  }
}

const execFileAsync = promisify(execFile)

/**
 * 起一个 PowerShell 跑脚本，**异步**等待。
 *
 * ── 为什么必须异步 ──
 *
 * 原来用 spawnSync：一次 OCR 会把宿主事件循环堵住 100~300ms（实测含启动更久）。
 * 那段时间里整个插件是卡的 —— 连 desktop_list_windows 都进不来；
 * 更糟的是"工具执行期间没有 await"会给人错觉，以为竞态不存在（评审第 4 条）。
 * execFile 返回 Promise，让出事件循环，行为才是代码看上去的样子。
 *
 * 注意：异步不等于"竞态问题解决了" —— 恰恰相反，让出事件循环才**真的**产生
 * 交错可能，所以提交前的那次有效性检查（index.mjs）现在是必须的，不是保险。
 */
async function runPowerShell(script, args, timeoutMs) {
  try {
    const { stdout } = await execFileAsync(PS, [
      '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script, ...args,
    ], { encoding: 'utf8', windowsHide: true, timeout: timeoutMs, maxBuffer: 4 * 1024 * 1024 })
    return decode(stdout)
  } catch (e) {
    // execFile 的错误形状：非零退出 / 超时被杀 / 程序不存在，都从 error 上区分。
    const code = String(e?.code ?? '')
    if (code === 'ETIMEDOUT' || e?.killed) {
      return { ok: false, error: 'Timeout', message: 'OCR 超时被终止' }
    }
    if (typeof e?.stdout === 'string' && e.stdout.trim()) {
      // 脚本自己打印了 JSON（比如 ok:false）但又以非零退出：那才是我们要的结果。
      const parsed = decode(e.stdout)
      if (parsed && parsed.ok === false) return parsed
    }
    return { ok: false, error: `Spawn: ${e?.message ?? String(e)}` }
  }
}

/**
 * 探测这台机器能不能 OCR、能读哪些语言。
 *
 * 调用方拿它决定"要不要提供视觉候选这条路"，以及**该怎么跟用户说**：
 * 没有中文语言包的机器上，中文界面 OCR 出来的东西是不可信的，
 * 那必须明说，而不是返回一堆乱码让模型去猜。
 */
export async function probeOcr() {
  const script = scriptPath()
  if (!script || !existsSync(script)) return { ok: false, error: 'NoOcrScript' }
  const res = await runPowerShell(script, ['-ProbeOnly'], 15000)
  const languages = Array.isArray(res.languages) ? res.languages : []
  return {
    ok: res.ok === true && languages.length > 0,
    backend: 'windows-ocr',
    languages,
    script,
    error: res.ok === true ? undefined : (res.error ?? 'NoOcrLanguage'),
  }
}

/**
 * 对一张**已登记的截图**跑 OCR。
 *
 * @param {string} imagePath  PNG 路径（调用方负责确认它来自某次截图登记）
 * @param {object} opts       { lang, timeoutMs }
 * @returns {{ok:boolean, backend:string, image:{width,height}, elapsedMs:number,
 *            languages:string[], engineLanguage?:string, lines:Array, error?:string}}
 */
export async function runOcr(imagePath, opts = {}) {
  const script = scriptPath()
  // 脚本必须真实存在：不存在就明确说"没有后端"，而不是让 PowerShell 去报一个
  // 含糊的"文件找不到"，再被我们自己翻译成"执行失败"。
  if (!script || !existsSync(script)) return { ok: false, error: 'NoOcrScript' }
  if (!imagePath || !existsSync(imagePath)) return { ok: false, error: 'ImageNotFound' }

  const envTimeout = Number(process.env.DSC_OCR_TIMEOUT_MS)
  const timeoutMs = Number.isFinite(opts.timeoutMs)
    ? opts.timeoutMs
    : (Number.isFinite(envTimeout) && envTimeout > 0 ? envTimeout : 20000)
  /* 路径必须归一成反斜杠绝对路径。
   *
   * 实测：WinRT 的 `StorageFile.GetFileFromPathAsync` 不接受正斜杠写法
   * （"C:/Users/…" 会抛 E_INVALIDARG，包在 IAsyncOperation 里表现成
   * "Wait 时发生一个或多个错误" —— 完全看不出是路径的问题）。
   * 同一个脚本从 bash 用 `-File … -Path C:\…` 跑是好的，一开始差点以为是脚本坏了。
   */
  const fullPath = String(imagePath).replace(/\//g, '\\')
  const args = ['-Path', fullPath]
  if (opts.lang) args.push('-Lang', String(opts.lang))
  // 不传 -Lang 时脚本走 TryCreateFromUserProfileLanguages：让系统按用户偏好挑，
  // 比"列表第一个"可靠（评审指出的问题）。真实用了哪种语言由 engineLanguage 回报。

  const res = await runPowerShell(script, args, timeoutMs)
  if (!res.ok) return res

  /* 尺寸自检：脚本报的宽高必须和 PNG 头里的一致。
   *
   * 不一致意味着后面所有 u/v 换算的基准是错的 —— 那比"OCR 失败"危险，
   * 因为它会安静地算出一个看起来合法的坐标。
   */
  let header = null
  try {
    const buf = readFileHead(imagePath, 64)
    header = buf ? pngSize(buf) : null
  } catch { /* 读不到就跳过校验，下面标 verifiedImage:false */ }

  const img = res.image ?? {}
  const consistent = !!(header && header.width === img.width && header.height === img.height)

  return {
    ok: true,
    backend: res.backend ?? 'windows-ocr',
    engineLanguage: res.engineLanguage ?? null,
    languages: Array.isArray(res.languages) ? res.languages : [],
    image: img,
    imageVerified: consistent,
    elapsedMs: Number.isFinite(res.elapsedMs) ? res.elapsedMs : null,
    lines: Array.isArray(res.lines) ? res.lines : [],
  }
}

/* 只读文件头：PNG 宽高在头 24 字节里，没必要把整张图读进内存两次。 */
import { openSync, readSync, closeSync } from 'node:fs'
function readFileHead(path, n) {
  let fd = -1
  try {
    fd = openSync(path, 'r')
    const buf = Buffer.alloc(n)
    const got = readSync(fd, buf, 0, n, 0)
    return got >= 24 ? buf.subarray(0, got) : null
  } catch {
    return null
  } finally {
    if (fd >= 0) try { closeSync(fd) } catch { /* ignore */ }
  }
}

/**
 * OCR 的行结果 → 候选的原料（原图像素坐标）。
 *
 * 这里只做"形状转换"，不做筛选：要不要按 query 过滤、要不要截断，
 * 都是上层的事。行框是**整行并集**，稀疏行会不准，所以同时把每个词的框带出去，
 * 上层要用更细的锚点可以取词框。
 */
export function linesToRows(lines) {
  const rows = []
  for (const line of lines ?? []) {
    const text = typeof line?.text === 'string' ? line.text : ''
    if (!text.trim()) continue
    const x = Number(line.x)
    const y = Number(line.y)
    const w = Number(line.w)
    const h = Number(line.h)
    if (![x, y, w, h].every((v) => Number.isFinite(v)) || w <= 0 || h <= 0) continue
    rows.push({
      label: text,
      // OCR 只给"行框"（四点矩形退化成轴对齐框）：用四角表示，保持和 OCR 一致
      quad: [[x, y], [x + w, y], [x + w, y + h], [x, y + h]],
      words: Array.isArray(line.words) ? line.words : [],
      // Windows OCR 不返回每行置信度；不编一个出来。
      score: Number.isFinite(line?.score) ? Number(line.score) : undefined,
    })
  }
  return rows
}
