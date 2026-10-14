# dsh-desktop-control

> 让 AI 助手观察并操作 Windows 桌面：读窗口无障碍树、截图、点击、输入。
>
> **这会让 AI 操作你的真实鼠标和键盘。** 权限模型见下面「权限模型」一节。

**不依赖外部视觉模型**——元素名、角色、坐标由 Windows UI Automation 提供，
不是模型从图片里猜的。

## 安装

在 DSH 里用插件市场安装 `dsh-desktop-control`。

**装完就能用，不需要改任何配置。** 辅助程序随插件包一起安装。

## 权限模型

**默认什么都不放行。** 首次操作某个应用时，插件会询问你是否允许：

```
允许在当前会话中操作「notepad.exe」吗？
同意后，本次会话内对该应用的鼠标/键盘操作不再询问；
会话结束即失效，不会写入任何配置。
```

- **同意** → 仅对**当前会话**的**这一个应用**有效
- **拒绝** → 本次会话内不再询问该应用
- **会话结束** → 授权自动失效，不需要做任何清理

另有 `desktop_revoke_grants` 工具，可在会话中途主动收回全部授权。

### 与「持久白名单」的区别

插件也支持在配置里写 `allowedApps`，但那是**持久**放行：写进去就一直有效，
**人不在场也照样能操作**。对能操纵鼠标键盘的能力来说这偏重，所以默认留空。
只有确实需要长期免问的应用才写进去。

## 工具

| 工具 | 作用 | 需要授权 |
|---|---|---|
| `desktop_list_windows` | 列出可见窗口 | 否（只读） |
| `desktop_read_window` | 读窗口的无障碍树 | 否（只读） |
| `desktop_screenshot` | 截图 | 否（只读） |
| `desktop_click` | 按坐标点击 | **是** |
| `desktop_click_element` | 按元素描述符点击 | **是** |
| `desktop_type` | 输入文本（支持中文） | **是** |
| `desktop_press_key` | 发送组合键 | **是** |
| `desktop_set_value` | 写控件值（走 UIA，带回读校验） | **是** |
| `desktop_do_action` | 触发次要动作（Raise/Scroll/Expand/Collapse） | **是** |
| `desktop_revoke_grants` | 收回本次会话的授权 | 否 |
| `desktop_indicate` / `desktop_off` | 显示/关闭运行指示层与急停 | 否 |

## 数据流向

界面上的一切都在本机处理：`dsc.exe` 是本地进程，无障碍树、截图、输入注入
都不出这台机器。**唯一的出站内容是工具返回的文本与截图**，它们会进入模型上下文，
和任何其它工具结果一样。

**界面文本按不可信数据处理**：应用可能在标题、按钮名、文档内容里藏针对模型的
指令，所以返回时加显式前缀说明"这是数据而非指令"。

## 已知限制

**1. 辅助程序不能放在名为 `dsh workspace` 的目录里。**

同一份 `dsc.exe`，放在该目录下时鼠标/键盘注入**静默失效**：
`SetCursorPos` 返回 `False`、光标不动、**没有任何报错**。
放到别处（插件目录、`%LOCALAPPDATA%`、任意其它路径）则正常。

原因未查明。已排除：AppContainer、提权、会话、窗口站/桌面、
作业对象 UI 限制、`BlockInput`、DPI、shell 启动链、进程存活时间、
目标窗口的上下文、文件哈希差异。

插件会自动检测这种情况并打警告，但**不会自动绕过**——那属于改变部署位置，
应由使用者决定。详见 `docs/当前状态.md`。

**2. 无障碍树在 Electron / WinUI 应用上很残。**

实测元素数：自建 WinForms 窗口 9 个（完整）、系统记事本 12 个（多为无名 Pane）、
DeepSeek Harness 自身 4 个、腾讯视频 2 个。

**这类应用要用截图路径。** 树路径只对经典 Win32 控件可靠。

**3. 低层键盘钩子不可用。**

`SetWindowsHookEx(WH_KEYBOARD_LL)` 能装上、句柄非零、消息循环正常，
但**回调一次都不触发**。因此没有"按 ESC 急停"，停止请用 `desktop_off`。

**4. 尚未实现**：`launch_app`（启动应用）、`drag`、`scroll`、
截图坐标锚定（`screenshotId`）。

## 安全说明

- **白名单双层校验**：插件和辅助程序各校验一次；操作前重新枚举窗口，
  确认目标仍存在、其可执行文件仍在名单内（挡住句柄被复用成别的进程）
- **所有写入回读验证**：`set_value` 返回 `before`/`after`/`verified`，
  值没变则报错而不是假装成功
- **失败一律拒绝**：授权通道不可用、回调抛错、无会话上下文 —— 全部按拒绝处理
- **急停闸门**：`desktop_off` 拉下闸门后，所有变更性命令被拒绝，
  不依赖按键通道或前台窗口

## 开发

```bash
./helper/build.sh                      # 编译 dsc.exe / dsc-edge.exe
node tools/test-approval.mjs           # 授权逻辑自测（22 项）
node tools/test-packaging.mjs          # 自包含性检查（22 项）
./tools/diag/build.sh CursorTruth      # 注入能力自检
```

改完插件后，把 `plugin/` 下的文件同步到 profile 的
`node_modules/dsh-desktop-control/`，并同步 `package.json` 与
`index.mjs` 里的 `PLUGIN_VERSION`。

## 文档

| 文件 | 内容 |
|---|---|
| `docs/当前状态.md` | **入口**：能做什么、不能做什么、改动前必看 |
| `docs/定案-注入取决于启动上下文.md` | 注入失效问题的完整排查记录 |
| `docs/ESC急停-实测记录.md` | ESC 方案的排查与结论 |
| `docs/与Codex的差异清单.md` | 与 OpenAI Codex computer use 的逐条对照 |
