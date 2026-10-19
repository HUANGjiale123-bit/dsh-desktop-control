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

**1. `dsc.exe` 不能放在带 Low 完整性标签的目录里（例如 `D:\dsh workspace`）。**

同一份 `dsc.exe`：放在工作区里跑是 **Low** 完整性级别，读 Chromium 系窗口只拿到
7 个节点（外壳）；同样字节复制到 `%TEMP%` 就是 **Medium**，读到 20~40 个。
`icacls "D:\dsh workspace"` 能看到 `Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)`
—— 从那里执行的程序也跟着变 Low，而 Low 完整性的 UIA 客户端拿不到内容树。

注入同理：低完整性下 `SetCursorPos` 失败、光标不动、**不报错**。

插件不受影响（它用 profile 安装目录里的那份，是 Medium）。
只有"直接跑 `helper/bin/dsc.exe`"（开发、调试、测试）才会踩到。
`ping` 和 `tree` 现在都会回报 `integrity` / `integrityRid`，**树读残时先看它**。
详见 `docs/定案-低完整性级别不是Chromium的毛病.md`。

**2. 无障碍树在 WinUI / 打包应用上仍然很残。**

实测元素数：自建 WinForms 窗口 9 个（完整）、系统记事本 12 个（多为无名 Pane）。
WinUI / 打包应用这类只有外壳，要用截图路径。

**Chromium 系（Electron / Tauri / WebView2）已经能读也能点**：插件会自动换一条
UIA 读取链（`server/uia-bridge.py`：python → PowerShell），实测 SuYou（Tauri）
读到 39~62 个有名字、有矩形的节点；`desktop_click_element` 在 Chromium 上会
自动改用这条链定位，然后优先用 `InvokePattern` 触发（不移动鼠标）。

详见 `docs/问题-Chromium无障碍-第十轮-按名字点击打通.md`。

**3. 停止用 ESC，不是 `desktop_off`。**

`dsc-esc.exe` 装上低层键盘钩子，按 ESC 拉下**闸门文件**；`dsc.exe` 每条命令执行前
都查这个文件，拦住所有变更性动作（连观察类命令也一并停）。
`desktop_off` 只收指示层界面，**不拉闸**。

**4. 尚未实现**：`visual-candidates`（在截图上标出候选元素、
带 `observationId` / `imageSha256` / 坐标仿射关系的那套）。
其余早先列为"未实现"的（`launch_app`、`drag`、滚轮、截图坐标锚定）都已实现。

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
| `docs/ESC强制退出-完整链路.md` | **ESC 停止的完整链路 + 六类踩过的坑** |
| `docs/实机验证-ESC打断操作.md` | **实机演示：人按 ESC 打断正在进行的操作** |
| `docs/定案-注入取决于启动上下文.md` | 注入失效问题的完整排查记录 |
| `docs/ESC急停-实测记录.md` | ESC 方案的排查与结论 |
| `docs/与Codex的差异清单.md` | 与 OpenAI Codex computer use 的逐条对照 |
