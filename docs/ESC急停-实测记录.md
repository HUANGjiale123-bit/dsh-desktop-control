# ESC 急停：实测记录

**结论：在这台机器上，本项目的进程收不到任何真实键盘输入。ESC 急停做不出来，
不是实现方式选错，是输入通道本身不向你打开。**

这份记录的价值在于：它把"试过什么、看到什么"固定下来，避免下次再从零开始试。

## 已排除的可能

| 假设 | 检验方式 | 结果 |
|---|---|---|
| 进程不在交互式桌面上 | `GetProcessWindowStation` / `GetThreadDesktop` / `OpenInputDesktop` | ❌ 排除。WinSta0 \ Default，与 `OpenInputDesktop()` 一致 |
| 与会话不同 | 对比 `SessionId` | ❌ 排除。本进程与 `explorer.exe` 同为 Session 43 |
| 集成方式不对（消息循环、STA、计时器） | `HookTest.cs` —— 最小控制台程序 + 阻塞 `GetMessageW` + 静态持有委托 | ❌ 排除。同样收不到 |
| 日志写入没工作，导致误判 | 心跳线程每 5 秒写一行 | ❌ 排除。心跳连续 7 次正常，日志通道是活的 |
| 钩子没装上 | `SetWindowsHookExW` 返回值 | 装上了，句柄非零（如 85133023），`UnhookWindowsHookEx` 也成功 |

## 试过的四种机制，全部失败

| 机制 | 结果 |
|---|---|
| `RegisterHotKey`（含挂在 message-only 窗口上） | 注册返回成功，**永不触发** |
| `GetAsyncKeyState` 轮询 | **从未读到过**任何键 |
| `GetKeyState` 轮询 | 只偶尔读到一次 ESC（`0xFF81`） |
| `WH_KEYBOARD_LL` 低级钩子 | **安装成功、句柄非零、心跳正常，但回调一次都没被调用** |

## 那个唯一的正信号，与其余数据矛盾

第一次探测里 `GetKeyState` 读到了 ESC（`raw=0xFF81`），但同一轮的
`GetAsyncKeyState` 和 `RegisterHotKey` 都没有。后续复测再没出现过。

`GetKeyState` 返回的是**调用线程消息队列的状态快照**。它能读到一次，说明按键
至少进过该线程的队列视野；但钩子（同样依赖消息队列投递）却完全收不到。
这两件事放在一起不自洽，我无法解释，因此**不把它当作"可以用"的依据**。

## 一个未能验证的关键对照

用户的 Codex 装有 Computer Use，其 ESC 急停按取证是
`SetWindowsHookExW(WH_KEYBOARD_LL)`、装在 session 级主 helper 里。
**如果 Codex 的 ESC 在用户按下去时是有效的，那"这台机器收不到钩子输入"这个
结论就不成立**，差异必然在启动方式上（谁是父进程、亲和性、作业对象、令牌）。

这个对照实验没做，因为需要用户配合并打断他正在做的事。**要做的话它是下一步**：
让 Codex 按一次 ESC 看它有没有反应。若它有效，就对比两个进程的父进程与令牌；
若它也无效，那结论就是这台机器确实不派发该输入。

## 诊断工具

都保留在 `helper/src/`，可用 `csc` 单独编译：

| 文件 | 用途 |
|---|---|
| `WinstaCheck.cs` | 打印窗口站 / 桌面 / 交互式桌面归属 |
| `HookTest.cs` | 最小 `WH_KEYBOARD_LL` 钩子，控制台 + 阻塞消息循环，带心跳 |
| `EscProbe.cs` | 三种机制并测（轮询两种 + 热键） |
| `EscProbe2.cs` | 逐项隔离对比，带毫秒时标 |

`dsc-edge.exe` 现在带 `--selftest-hook`（只装钩子并报告，不显示指示层）与
`--no-esc`（跳过急停）。急停代码保留在 `EscapeGate` 类里，**但它在这台机器上
永远不会触发**。要恢复这个功能，先做上面那个未验证的对照。
