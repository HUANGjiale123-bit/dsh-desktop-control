# ESC 急停：实测记录（最终版）

**结论：本项目的进程收不到任何低层钩子投递。ESC 急停在当前启动方式下做不出来，
不是 API 选错，也不是集成方式不对。**

## 决定性证据：自注入对照

最关键的一个实验是**自注入对照**——装好键盘钩子后，由本进程自己用 `SendInput`
注入按键，看自己的钩子能否收到：

```
注入 'Z' 'x' '9' 'Q' → SendInput 全部返回成功
（自注入结束 2 秒后）原始回调 = 0，其中注入 = 0，物理 = 0
```

这与"物理按键收不到"有本质区别：

- 如果自注入**能**收到 → 钩子链路完好，收不到的只是物理输入（与输入来源有关）
- 自注入**也**收不到 → **钩子在本进程内根本不工作，与输入来源无关**

实测是后者。

## 所有被排除的可能

| 假设 | 检验方式 | 结果 |
|---|---|---|
| 进程不在交互式桌面 | `GetProcessWindowStation` / `GetThreadDesktop` / `OpenInputDesktop` | ❌ 排除：WinSta0 \ Default，与输入桌面同名 |
| 与会话不同 | 对比 `SessionId` | ❌ 排除：与 `explorer.exe` 同为 Session 43 |
| 作业对象 UI 限制 | `QueryInformationJobObject(JobObjectBasicUIRestrictions)` | ❌ 排除：在作业里，但 `UIRestrictionsClass = 0x00000000` |
| 集成方式不对（WinForms/动画线程） | `HookTest.cs`、`HookBranch.cs`：最小控制台程序 + 阻塞 `GetMessageW` | ❌ 排除：同样收不到 |
| 装钩线程与泵线程不一致 | 打印装钩 tid，消息循环在同一线程 | ❌ 排除 |
| 消息循环写法错误 | 改用 `MsgWaitForMultipleObjects` + `PeekMessage(hwnd=NULL)` | ❌ 排除 |
| 日志没写导致误判 | 心跳线程每 5 秒一行，连续 8 次正常 | ❌ 排除 |
| 线程被饿死导致静默摘钩 | `SetThreadPriority(TIME_CRITICAL)` | ❌ 排除 |
| 只对键盘失效 | 同时装键盘 + 鼠标钩子 | ❌ 排除：两者都是 0 |
| 焦点/前台窗口影响投递 | 点桌面让 DSH 失焦后再按键 | ❌ 排除：前台窗口确实变了，回调仍为 0 |
| `dwThreadId` 传错 | 传 0（全局钩子） | ❌ 排除 |
| 输入是合成的 | 回调分别统计 `LLKHF_INJECTED` 前后数量 | ❌ 排除：原始计数就是 0 |

## 试过的四种机制，全部失败

| 机制 | 结果 |
|---|---|
| `RegisterHotKey`（含挂在 message-only 窗口上） | 注册返回成功，**永不触发** |
| `GetAsyncKeyState` 轮询 | **从未读到过**任何键 |
| `GetKeyState` 轮询 | 只在一次探测里读到过一次 ESC（`0xFF81`），复测再未出现 |
| `WH_KEYBOARD_LL` / `WH_MOUSE_LL` 低级钩子 | 安装成功、句柄非零、消息循环正常，**回调一次都没被调用** |

## 一个曾经误导我很深的坑

早期几轮测试里，`SetWindowsHookExW` 返回的是**非零句柄**，我据此以为"钩子装上了"。
后来发现那个诊断程序在装钩**之前**就崩了——`CompareObjectHandles` 这个 API
经动态解析确认**本机 kernel32 没有导出**（Windows 11 26200），
P/Invoke 直接抛 `EntryPointNotFoundException`，而且异常连 `ToString()` 都失败，
于是整个程序哑崩。

**所以我读到的"句柄"是无意义的值，"0 次回调"也不是钩子的结论。**

这直接说明了一件事：**句柄非零只证明参数合法，不证明钩子会收到事件**——
这正是 Codex 用 fail-closed 的理由。

## 与 Codex 的差异（未解）

Codex 的 ESC 急停按取证是 `SetWindowsHookExW(WH_KEYBOARD_LL)`，装在
session 级主 helper（`codex-computer-use.exe`）里，父进程是 `node_repl` 内核，
再往上是 `ChatGPT.exe`。

我们的环境在窗口站、桌面、会话、作业对象 UI 限制这四项上与它没有区别。
**剩下的唯一变量是启动链**——但这一点尚未验证，因为需要用户配合让 Codex
按一次 ESC 看它有没有反应。那个实验很便宜，是下一步。

## 诊断工具

都在 `helper/src/`，用 `csc` 单独编译：

| 文件 | 用途 |
|---|---|
| `HookSelfTest.cs` | **最有价值的一个**：自注入对照，区分"钩子坏了"与"输入来源问题" |
| `HookBoth.cs` | 键盘 + 鼠标钩子并测，区分"只对键盘失效" |
| `HookBranch.cs` | A/B 分支：切桌面 vs 不切桌面 |
| `HookDiag.cs` | 桌面身份 + 装钩线程 + 双计数，按 Codex 的做法实现 |
| `HookRaw.cs` | 零过滤回调计数 |
| `WinstaCheck.cs` | 窗口站 / 桌面 / 输入桌面归属 |
| `ApiProbe.cs` | 按模块导出表查 API 是否存在（避免再次踩 `CompareObjectHandles` 那种坑） |

`dsc-edge.exe` 带 `--selftest-hook`（只装钩子并报告）与 `--no-esc`（跳过急停）。
`EscapeGate` 类保留在代码里，**但在这台机器上永远不会触发**。

## 对"怎么停下来"的影响

急停闸门本身是可靠的——已验证能拦住全部变更性命令。缺的只是**触发器**。
既然本进程收不到按键，触发器只能来自本进程之外：

- `desktop_off`（模型调用）—— 当前唯一可用的方式，已验证
- 外部程序写 `<repo>\.state\halt` —— 闸门路径固定，任何能写文件的程序都能触发
- 任何"帮我按一下"的全局热键，只要它不依赖本进程的钩子链
