# 与 Codex 的差异清单

用户要求"都按照 Codex 来"。这份清单逐条对照取证内容与当前实现，
标出差距、影响、以及是否要改。

取证来源：用户对 `OpenAI.Codex 26.1007.2314.0` 的逆向记录（见对话），
以及 `docs/ESC急停-实测记录.md`。

---

## 一、方法集合差异（sky / window2）

Codex 的 Windows 方法表：
```
activate_window  list_apps        list_windows    get_window
get_window_state click_element    set_value       perform_secondary_action
click            scroll           drag            type_text
press_key        launch_app       close           end_turn
diagnostic_state start_audio_recording stop_audio_recording
```

| Codex 方法 | 我有的 | 差距 | 影响 |
|---|---|---|---|
| `list_windows` | ✅ `list_windows` | 无 | — |
| `get_window` | ❌ | **缺**：按 id 重新取回窗口，句柄失效后恢复 | 中：句柄变了就断链 |
| `get_window_state` | ⚠️ `tree` + `screenshot` 分成两个命令 | **结构不同**：Codex 一次返回 `{window, accessibility, screenshots[]}` | 高：状态不同源 |
| `click_element` | ✅ `click_element` | 但我用描述符重新定位，Codex 用索引 | 中：慢、且索引语义不同 |
| `set_value` | ✅ | 无 | — |
| `perform_secondary_action` | ✅ `do_action` | 无（七个标签一致） | — |
| `click`（坐标） | ✅ `click` | **缺 screenshotId 锚定** | **高：窗口重叠就废** |
| `scroll` | ❌ | **缺** | 中 |
| `drag` | ❌ | **缺** | 低 |
| `type_text` | ✅ `type` | 无（已修 Unicode） | — |
| `press_key` | ✅ | 按键命名不同（见下） | 中 |
| `activate_window` | ❌ | **缺，且这是注入可靠性的关键** | **高** |
| `launch_app` | ❌ | **缺** | **高：无法启动应用** |
| `close` | ❌ | 缺 | 低 |
| `end_turn` | ⚠️ 有 `halt`（闸门），语义不同 | 中 |
| `list_apps` | ❌ | 缺（有 list_windows） | 中 |
| `diagnostic_state` | ⚠️ 有 `ping` | 字段少 | 低 |
| 音频录制 | ❌ | 不做 | — |

---

## 二、`get_window_state` 的默认值是反的（**要改**）

Codex 的原文：

> By default, `get_window_state({ window })` captures and automatically
> displays a screenshot, and returns `accessibility: null`.
> **This is the best default for desktop apps with weak accessibility trees.**

| | Codex | 我 |
|---|---|---|
| `include_screenshot` 默认 | **true** | 树优先，截图另调 |
| `include_text` 默认 | **false** | 总是返回树 |
| 两者都为 false | 报错 `get_window_state must request include_text, include_screenshot, or both` | 无此校验 |

**我实测印证了 Codex 的判断**：腾讯视频（Electron）只暴露 2 个元素，
记事本（Store 版）暴露 12 个无名 Pane——树在这些应用上没用，截图才有用。

**要改**：截图应该成为默认，树按需请求。

---

## 三、坐标锚定：`screenshotId`（**要改，实测被它卡住过**）

Codex 的做法：

```ts
type ClickInput = {
  window: Window;
  x?: number; y?: number;          // 窗口相对坐标
  screenshotId?: string;           // 有这个时，坐标按该截图的坐标系解释
  element_index?: number;
  mouse_button?: MouseButton;
  click_count?: number;
};
```

> 若同时传入 `screenshotId`，坐标按**该截图自己的坐标系**解释，
> helper 会把它映射回窗口/屏幕坐标。

`Screenshot` 结构：
```ts
{ id, zIndex, url, originX?, originY?, width?, height? }
// 屏幕坐标 = originX/originY + 截图坐标
// width/height 是逻辑像素
```

**我实测到的后果**：DSH 窗口盖住腾讯视频时，
`WindowFromPoint(搜索框坐标)` 返回 DSH，白名单校验直接拒绝——**正当操作被挡住**。
Codex 因为有 `screenshotId` 锚定，z 序和重叠都不影响。

**要改**：截图带 id + origin + 尺寸，`click`/`scroll`/`drag` 接受 `screenshotId`。

---

## 四、动作后的刷新纪律（**已部分做到**）

Codex 的硬规则：

> Element indexes are valid only for the accessibility state that produced them.
> Refresh accessibility state after any action that may change the visible element tree.

以及两格循环：**观察 → 停 → 一个动作 → 立刻刷新**。

| | Codex | 我 |
|---|---|---|
| 索引时效性 | 强制（runtime ID / 进程 / 目标窗口三重校验 + 快照版本） | ⚠️ 有 `snapshotRevision`，但一次性进程下**无法跨调用校验** |
| 动作后刷新 | 每次动作后立刻重取状态 | ❌ 不自动刷新 |
| 失败语义 | `Input or refresh outcome is unknown; reobserve before retrying` | ❌ 无 |

**我的实测印证**：点按钮后按钮文字变了（`点我` → `点了 1 次`），
但那是我主动重读才看到的。若索引在动作后错位，我没有机制发现。

**要改**：`click_element` / `set_value` / `do_action` 完成后回读状态，
把"动作后状态"一并返回（我已对 `set_value` 做了回读，其余没有）。

---

## 五、错误码与文案（**要统一**）

Codex 的 sky 错误码（`ServerErrorCode`）：

| 码 | 名称 | 我的对应 |
|---|---|---|
| -10006 | `appNotAllowed` | ✅ 白名单拒绝 |
| -10007 | `runningApplicationNotFound` | ⚠️ 窗口不存在 |
| -10008 | `accessibilityError` | ✅ `UiaError` |
| -10010 | `invalidApp` | ❌ |
| -10011 | `noActiveSession` | ⚠️ 有 `halt` |
| -10012 | `userStoppedSession` | ❌ |
| -10016 | `userIntervened` | ❌ |
| -10018 | `ambiguousApp` | ❌ **实现 `launch_app` 时必须** |
| -10020 | `screenLocked` | ❌ **要加，我撞到过锁屏** |

Codex helper 的错误文案（原文）：

```
Computer Use is disabled by policy
Computer Use is not allowed on this app by policy
Computer Use cannot operate on <...>
Computer Use requires approval to use <...>
Computer Use is no longer available in this turn because the turn has ended.
Computer Use was stopped by the user with the physical Escape key.
Accessibility is limited because the target window has higher Windows integrity
  than the Computer Use helper.
window bounds changed; call get_window_state before using this window
window is minimized; call activate_window, refresh with get_window, then retry
  get_window_state
element <N> no longer matches the cached runtime ID
element <N> no longer belongs to the cached target process
unknown screenshotId <...>
no screenshot targets found for <window>
cannot set a value for an element that is not settable
unsupported secondary action: <X>; expected Raise, Scroll Up, ...
```

| 我的错误码 | 对应 Codex | 要改吗 |
|---|---|---|
| `NotSettable` | `Cannot set a value for an element that is not settable` | 文案已一致 |
| `ActionNotAvailable` | `secondary action '<X>' is not available for element <N>; available actions: <...>` | 已一致 |
| `BadAction` | `unsupported secondary action: <X>; expected ...` | 已一致 |
| `StaleSnapshot` | `element <N> no longer matches the cached runtime ID` | **文案不同** |
| `UiaError` | `accessibilityError` | 语义同，名字不同 |
| `Refused`（密码） | — | 我自加的，保留 |
| `RangeUnavailable` | — | 我自加的，保留（实测必要） |

---

## 六、`activate_window` —— 注入可靠性的关键（**要高优先级加**）

Codex 的原文：

> `activate_window(input: {window}): Promise<void>` —— **可选逃生口**，
> 把窗口提到前台；**输入类方法会自动激活目标窗口**。

以及恢复流程：

```
window is minimized; call activate_window, refresh with get_window,
  then retry get_window_state
; activate the target or take a fresh screenshot before retrying
```

**这解释了我实测到的全部"注入时好时坏"**：

- 我的 `type` / `press_key` 有焦点校验，但**没有主动激活**——切前台失败就直接报错
- `click` / `click_element` **完全没有前台检查**，直接注入
- 而 `SetForegroundWindow` 从后台进程调用**会被系统拒绝**，我却一直在试

**要改**：
1. 加 `activate_window` 能力（`AttachThreadInput` + 重试）
2. 所有输入类方法注入前**自动激活目标窗口**
3. 激活失败明确报错，不进入"注入被压"的坏状态
4. 恢复流程照 Codex：失败 → 激活 → 重取状态 → 重试一次

---

## 七、`launch_app`（**要加，这是我最早卡住的地方**）

Codex 的标识体系（两套）：

- **目录标识**：从 `shell:AppsFolder` 枚举，缓存到 `shell-apps.json`，
  字段 `display_name` / `identifier` / `target_path` / `process_keys` /
  `last_used` / `use_count` / `install_state`
  （打包应用的 identifier 是 `shell:AppsFolder\<AUMID>`）
- **进程标识**：`window.app` 可能是进程路径

错误原文：
```
launch_app requires an installed app id from list_apps or a launchable
  executable identifier
launch_app does not support pid app identifiers
launch_app has no approved target
app identifier <X> is ambiguous; use the canonical app id from list_apps
```

白名单键与 `launch_app` 参数**不是同一命名空间**：
白名单键更像进程/二进制名（`qqlive.exe`）。

**要改**：加 `list_apps` + `launch_app`，接受 AUMID / `.lnk` / exe 路径，
**拒绝 pid 形式**，**不在 basename 上做启动匹配**（那正是 `ambiguousApp` 的来源）。

---

## 八、按键命名（**要改**）

Codex 用 X Window System keysym 风格：

> `a`、`space`、`Return`、`Tab`、`Control_L+a`、`Control_L+Shift_L+period`、
> `KP_0`；空白被忽略；接受别名 `Control`/`Ctrl`/`Alt`/`Shift`/`period`/
> `greater`/`Numpad_0` 等。
> 对需要 Shift 的标点快捷键要显式写 `Shift`，例如 `Control_L+Shift_L+period`。

| | Codex | 我 |
|---|---|---|
| 修饰键 | `Control_L` / `Shift_L`，接受 `Ctrl` / `Shift` 别名 | `Ctrl` / `Shift` ✅ |
| 主键 | keysym 名（`period`、`KP_0`、`Return`） | 字符键名 |
| 数字键盘 | `KP_0`..`KP_9` | ❌ 无 |

**要改**：加 keysym 别名表（至少 `Return`/`Escape`/`space`/`period`/`KP_x`）。

---

## 九、完整性级别检查（**要加**）

Codex 的实现：

- 读自身 IL：`currentProcessIntegrityLevel`
- 读目标 IL：`OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)`
- 目标更高时的文案：
  `Accessibility is limited because the target window has higher Windows integrity
   than the Computer Use helper.`

**用户取证的结论**：不存在"读受限但写仍可"，应按**基本不可用**处理。

**要改**：`RequireAllowedWindow` 里加 IL 比较，目标更高时明确报错交还。

---

## 十、截图：多张 + 排除系统窗口（**要改**）

Codex 的 `screenshots` 是**数组**，每张是一个有界区域：

> "Bounded screenshots captured for the window and related transient UI"
> 每张带 zIndex（层序）、originX/originY（屏幕原点）、width/height（逻辑像素）
> 一个顶层 HWND 一个 capture item ≈ 一张截图

排除的系统窗口（原文）：
```
Backstop Window、Default IME、Input Occlusion Window、MSCTFIME UI、
Program Manager、Taskbar、ApplicationFrameHost.exe、LockApp.exe、_proxy.exe
```

底层用 **Windows.Graphics.Capture**（逐窗口捕获），不是 GDI 区域抓取。

| | Codex | 我 |
|---|---|---|
| 捕获方式 | WGC 逐窗口 | GDI `CopyFromScreen` 区域 |
| 被遮挡时 | 仍能抓到目标窗口 | **抓到遮挡者**（实测被坑过） |
| 多张 | 主窗口 + 瞬态 UI | 单张 |
| 排除系统窗口 | 有列表 | ❌ 我列出了 Taskbar/Program Manager |

**要改**：至少加排除列表；WGC 改动大，列为后续。

---

## 十一、锁屏与桌面（**要加**）

Codex 的文案与 API：

```
Windows desktop is locked (input desktop is <name>); unlock before using computer-use
OpenInputDesktop / GetThreadDesktop / SetThreadDesktop / CloseDesktop
DefaultWindows / Default Lock Screen / Winlogon
```

以及：**项目组曾加过 `SetThreadDesktop(输入桌面)` 且实测有害**（见 ESC 记录）。

**要改**：加锁屏检测（输入桌面名不是 `Default` 就报错交还），
**但不做 SetThreadDesktop**——那已被实测否定。

---

## 十二、已被实测否定的差异（**不要照抄**）

| Codex 的做法 | 我的实测结论 |
|---|---|
| `SetThreadDesktop(输入桌面)` | **有害**：绑定后注入从"被接受但无效"退化为"拒绝访问" |
| `CompareObjectHandles` 判桌面身份 | **本机 kernel32 无此导出**，P/Invoke 直接崩 |
| 低层键盘钩子做 ESC 急停 | 钩子装得上、句柄非零、**回调一次都不触发**（Codex 的却能工作，差异未解） |
| 依赖 `SetCursorPos` 的 last-error | **该 API 不设置 last-error**，读到的都是陈旧值 |

---

## 优先级

| 级别 | 项 | 理由 |
|---|---|---|
| **P0** | `activate_window` + 输入前自动激活 | 实测这是"注入时好时坏"的根因 |
| **P0** | 截图成为默认；`get_window_state` 合并 | 弱树应用上树没用，实测印证 |
| **P0** | 完整性检查 | 安全边界，且能解释"读得到写不了" |
| **P1** | `screenshotId` 坐标锚定 | 实测被窗口重叠卡住 |
| **P1** | `launch_app` + `list_apps` | 最早卡住的能力缺口 |
| **P1** | 动作后回读状态（统一） | 索引时效性只能靠回读保证 |
| **P2** | 错误码/文案统一 | 诊断价值 |
| **P2** | 按键 keysym 别名 | 兼容性 |
| **P2** | 截图排除系统窗口 | 降噪 |
| **P3** | `scroll` / `drag` / `close` / `get_window` | 能力补齐 |
| **P3** | WGC 逐窗口捕获 | 改动大 |
| **不做** | `SetThreadDesktop`、`CompareObjectHandles`、ESC 钩子 | 已实测否定 |
