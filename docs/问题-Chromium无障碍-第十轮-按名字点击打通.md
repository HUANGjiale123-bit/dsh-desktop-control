# Chromium 无障碍 · 第十轮：按名字点击打通（2.18.31）

> 一句话：**现在能按名字点 Chromium 系窗口里的元素了**，不再只是"看得到、点不了"。
> 实测靶子 SuYou（Tauri）：点「我 的」→ 界面切到我的页 → 点「首 页」→ 切回首页。

## 上一轮卡在哪

第八轮定位到根因：`AutomationElement.FromHandle` 在 .NET 调用方手里会给一个
`IsControlElement=True`、`Descendants=0` 的空壳，控件视图不再展开，
`FindAll` 返回 0，于是 `FindByDescriptor` 只扫得到根节点 —— **任何描述符都是 NotFound**。

第九轮把"读"绕过去了：DSH（Node，非 .NET）直接起 python，python 再起
PowerShell 做 UIA，绕开整条 .NET 进程树。读到 39~62 个有名字、有矩形的节点。

但**能读 ≠ 能点**。`desktop_click_element` 走的还是 `dsc.exe click_element`，
而 dsc.exe 是 .NET 进程 —— 它自己起不了那条桥（实测 `.NET → python → powershell`
只有 5 个节点）。所以"树里有这一行、按名字点却说找不到"，是本项目里最气人的那种状态。

## 这一轮的解法：把"点"也拆成两半

```
desktop_click_element
   └─ dsc.exe click_element        ← 先试，失败就是 Chromium 那种空壳
        └─ NotFound
             └─ UIA 桥（python → PowerShell，非 .NET）
                  ├─ 找到元素、支持 InvokePattern → **桥里直接 Invoke**（不动鼠标）
                  └─ 不支持 → 只返回元素的物理矩形
                       └─ dsc.exe click x y --hwnd   ← 鼠标动作仍由它做
```

**为什么坐标点击不放在桥里**：PowerShell 默认不是 DPI 感知进程，坐标会被系统
按缩放比虚拟化；而 dsc.exe 有 Per-Monitor V2 清单，`click` 那条路径已经验证过，
还会检查"落点是否真的属于目标窗口"。两条链各干自己可靠的那件事。
（桥这一侧也补了 `SetProcessDpiAwarenessContext(-4)`，让返回的矩形就是物理像素。）

**为什么只对 `NotFound` 回退**：

| helper 的失败 | 会不会换桥 | 原因 |
|---|---|---|
| `NotFound` | 会 | 换条链再找一遍，不产生副作用 |
| `AmbiguousTarget` | 不会 | helper 看见多个，桥只会看见同样多个，不猜 |
| `UnknownOutcome` | 不会 | Invoke 已经发出去，再试一次就是重复触发 |
| `Halted` | 不会 | 闸门在前一步就拦住了 |

## 匹配规则：和"读树"是同一个集合

桥里按 `FindAll(Descendants, TrueCondition)` 的全部节点 + 候选根去找，也就是
`desktop_read_window` **列出来的那一批**。刻意不另走一套遍历 ——
否则会出现"树里明明有这一行，按名字点却说没有"。

字段规则与 `Program.cs` 的 `FindByDescriptor` 一致：
`role` = ControlType 去掉前缀、`name` = Name、`aid` = AutomationId、`class` = ClassName，
全部**大小写不敏感全等**（PowerShell 的 `-eq` 默认就是大小写不敏感）。

找不到时不再只说"没找到"，而是给出**附近的名字**：

```
未找到匹配元素：role=Button/name=充值2
  两条链都找过了：dsc.exe 与 UIA 桥（桥在那个窗口里读到 40 个节点）。
同一范围里**其余条件都对上**的元素，名字是这些（多半是 name 写错了）：
  · "充值"
  · "重新匹配最快网络"
  · "首 页"
  · "我 的"
```

这条是刻意做的：`nearbyKind=sameRole` 表示 role/aid/class 都对上了、只有 name 没对上，
那基本就是名字写错，照着改一行即可，不用重新读一遍树。

## 顺带修掉的两个 bug

### 一、helper 会**把同一个按钮点两次**（严重，只在真实用户机器上出现）

```csharp
method = "InvokePattern" + cursorNote;   // 成功时带上光标说明
...
if (method == "InvokePattern") { ... return 0; }   // ← 这里为假
Native.RECT r; GetElementRect(found, out r);
MoveCursorAbsolute(cx, cy); SendMouseButton(right, 1);   // ← 于是又点了一次
```

Invoke 一次 + 坐标点击一次 = **点两次提交、发两条消息**。而且返回的 JSON 里
`method` 写着 `CoordinateClick`，看不出前面已经 Invoke 过。

为什么一直没被发现：这台机器上 `SetCursorPos` 在沙箱里返回 5（拒绝访问），
光标没动，`cursorNote` 一直是空串，那条分支从来没被走到。
换句话说，**这个 bug 只在鼠标能真的移动的机器上出现**。

修法：用布尔量 `invoked` 决定分支，`method` 固定是 `"InvokePattern"`，
光标说明单独放 `cursorNote`（并写进 `cursorMoved` 字段）。plugin 侧的
`r.method === 'InvokePattern'` 也改成了 `startsWith`，两端都不再靠字符串精确相等做判断。

### 二、桥的 stdin 没强制 UTF-8，中文描述符在入口就被解错

桥的常驻模式按行读 stdin，而 Windows 上的 python 用系统 ANSI（GBK）解码 stdin。
调用方发的是 UTF-8，于是：

```
"我 的"  →  "鎴� 鐨�"
→ 未找到匹配元素：role=Button/name=鎴� 鐨�
```

JSON 结构完全合法、名字只是不对，看起来像"元素不存在"，其实是编码在入口就错了。
stdout 那条早就修过（`UnicodeEncodeError`），stdin 这条一直没修 —— 因为
之前没有人用中文描述符走常驻模式。

## 急停闸门：桥也必须查

桥是另一条链（Node 直接起 python），完全不经过 dsc.exe。不查闸门的后果是
**"按了 ESC，界面收起来了，桥照样把按钮点下去"** —— 正是本项目反复踩到的
"看起来停了其实没停"。

现在 toolset 每次起桥都传 `DSC_HALT_GATE`，桥在做**变更性动作之前**检查该文件
（读树不查：观察不改变任何东西，拦住它只会让人看不见现场）。

## 实测记录

环境：Windows 10.0.26200，SuYou `hwnd=66988` `class=Tauri Window` 688x1055。

| 步骤 | 结果 |
|---|---|
| `readWindow` | 39 个节点，25 个有名字，走桥 |
| dry-run `role=Button/name=我 的` | 命中 `Button "我 的"` @ 1586,1073 252x84，`hasInvoke=true` |
| 真点「我 的」 | `method=InvokePattern`，界面切到"我的"页（树变 63 个节点，出现「退出登录」） |
| 真点「首 页」 | 切回首页（40 个节点，「剩余流量」回来了） |
| `role=Button/name=充值2` | `NotFound` + nearby = 充值 / 重新匹配最快网络 / 首 页 / 我 的 |
| `role=Text` | `AmbiguousTarget`，命中 15 个，给了前 6 个候选 |
| 闸门文件存在时点 | `Halted`，没有真的点 |

`tools/test-bridge-click.mjs`：**21 项通过**（含上面这次往返，测试自己会把界面点回首页）。

## 还没验的（别当成已经成立）

1. **坐标点击那条分支没有端到端验过。** 桥给出正确矩形、`dsc.exe click` 也收到了
   正确坐标，但本机沙箱里 `SetCursorPos` 直接返回拒绝访问（错误 5），
   鼠标根本没动 —— 只验到"失败被如实报告"，没验到"点下去真的生效"。
   SuYou 的按钮全都 `hasInvoke=true`，所以这台机器上主要走 Invoke 分支。
2. **DPI 只在 100% 缩放下测过。** 桥里已声明 Per-Monitor V2，
   但"125%/150% 下矩形与 dsc.exe 一致"这件事没有实测数据。
3. **只测了一个 Tauri 应用。** Electron / WebView2 的匹配与 Invoke 支持情况未知。
   能读到树的那批（DSH 自己、微信）还没点过。
4. **换机器未测**（一直挂着的 B 项）。
