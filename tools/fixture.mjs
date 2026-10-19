// 测试夹具：把 helper 复制到**工作区外**再跑。
//
// ── 为什么必须这么做 ──
//
// `D:\dsh workspace`（DSH 的工作区）带着 `Mandatory Label\Low Mandatory Level` 标签，
// 而从那里执行的程序**也以 Low 完整性运行**。实测同一份 dsc.exe：
//
//     工作区里跑   → Low    → 读 Chromium 只有 7 个节点（外壳），SetCursorPos 被拒
//     %TEMP% 里跑  → Medium → 读到 20~40 个节点，注入正常
//
// 也就是说：测试跑在仓库里时，helper 一直是 Low，**测的不是用户实际会跑的那条路径**。
// 更糟的是有些断言会因此"看起来通过"（比如 readWindow 走了桥、坐标点击被拒绝），
// 而真实环境里根本不会那样。
//
// 所以凡是要断言"读到什么/点成没成"的测试，都该用这里给出的 helperPath。
// 要断言"Low 下的行为"的（例如 docs/定案-低完整性级别… 里那条），就继续用
// helper/bin/dsc.exe —— 两种路径都要有人测，但不能混。
//
// 用法：
//   import { tempHelper } from './fixture.mjs'
//   const helperPath = tempHelper()            // 已复制到 %TEMP%，进程退出时自动清
//   createDesktopToolset({ helperPath, ... })

import { copyFileSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { spawnSync } from 'node:child_process'

/** 源 helper（仓库内，Low 标签目录下）。 */
export const REPO_HELPER = 'helper/bin/dsc.exe'

let cached = null

/**
 * 保证 helper 跑在 Medium 下：复制到系统临时目录，返回那份副本的路径。
 * 同一个进程里只复制一次。退出时清理。
 */
export function tempHelper() {
  if (cached) return cached.helperPath

  // mkdtempSync 给的是 0700 的临时目录，且**不在**工作区标签的继承链上。
  const dir = mkdtempSync(join(tmpdir(), 'dsc-helper-'))
  const helperPath = join(dir, 'dsc.exe')
  copyFileSync(REPO_HELPER, helperPath)
  cached = { dir, helperPath }

  process.on('exit', () => {
    try { rmSync(dir, { recursive: true, force: true }) } catch { /* 收不掉就留给系统 */ }
  })

  return helperPath
}

/** 读某个 helper 副本自报的完整性级别（Low / Medium / …）。 */
export function integrityOf(helperPath) {
  const r = spawnSync(helperPath, ['ping'], { encoding: 'utf8', windowsHide: true, timeout: 60000 })
  try {
    return String(JSON.parse(String(r.stdout ?? '{}')).integrity ?? 'unknown')
  } catch {
    return 'unknown'
  }
}

/**
 * 自检：临时副本必须**不是** Low。
 *
 * 如果这里也返回 Low，说明这台机器的临时目录同样被打了标签（或者 mkdtemp 落到了
 * 工作区里），那么后面所有"Medium 下应当如何"的断言都不成立 —— 与其跑出一堆
 * 误导性的绿色，不如当场停下并说清楚。
 */
export function assertNotLow(helperPath) {
  const lvl = integrityOf(helperPath)
  if (lvl === 'Low') {
    throw new Error(
      `测试夹具失效：临时副本 ${helperPath} 仍然是 Low 完整性 —— ` +
      `这台机器的临时目录可能也被打了 Low 标签，改用 Medium 的断言都不成立。`)
  }
  return lvl
}
