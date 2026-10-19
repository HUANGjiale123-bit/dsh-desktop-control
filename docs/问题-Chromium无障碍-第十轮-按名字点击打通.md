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

## 重启后的实机验证（走真插件，不是脚本）

把 2.18.31 同步到 profile 并重启 DSH 之后，直接用插件自己的工具验：

| 调用 | 结果 |
|---|---|
| `desktop_list_windows` | SuYou 仍在 hwnd=66988（pid 10376） |
| `desktop_read_window({hwnd:66988, maxNodes:40})` | **40 个元素**，带 `aid=`、`focusable`、`Secondary Actions`、`[value:settable]` |
| `desktop_click_element({descriptor:'role=Button/name=我 的'})` | `InvokePattern` 成功，报告"光标已移到元素上"，截图确认切到"我的"页 |
| `desktop_click_element({descriptor:'role=Button/name=首 页'})` | 切回首页 |

**这次点击正好踩在旧 bug 的触发条件上**：`SetCursorPos` 真的成功了
（报告里是"光标已移到元素上"，不是"没有移动鼠标"），旧代码在那种情况下会把
`method` 拼成 `"InvokePattern，光标已移到…"`，于是继续走坐标分支再点一次。
现在返回值是 `method=InvokePattern`、`cursorMoved=true`，页面只切换了一次 ——
这就是那个"点两次"的修在真实环境里的验证。

### 附带发现：dsc.exe 看不看得见，还取决于**谁启动它**

同一时刻、同一台机器上：

| 启动链 | `dsc.exe tree 66988` | 桥 |
|---|---|---|
| `explorer → DSH → dsc.exe`（插件那条） | **40 个元素**，能按名字点 | —（没被用到） |
| `explorer → DSH → bash → powershell → dsc.exe`（agent shell 那条） | **7 个元素**（Pane=5, TitleBar=1, Window=1） | 39 个 |

所以"Chromium 读不到"不是目标应用的单方面属性，**调用方的进程上下文也参与决定**。
机制没查清（第八轮那个 .NET 链效应只能解释一部分，这两条链里都没有 .NET）。

实用结论：**两条链都不能删**。插件那条现在能直读，但它会因为启动方式而变瞎；
桥那条更稳（39 个节点），慢 300~800ms。现在的实现正是"helper 先说，说不出就换桥"。

## 第二轮实机验证：坐标点击、Electron、DPI

上表都是 InvokePattern（不动鼠标），所以"坐标点击到底能不能点成"当时还没证据。
这一轮专门补这三块。

### 1. 坐标点击：点成了

```
desktop_click_element({ hwnd:66988, descriptor:'role=Text/name=剩余流量' })
→ 已按描述符 "role=Text/name=剩余流量" 定位到元素（矩形 1389,303 85x28），
  并在其中心 (1431,317) 执行 left 键点击。
  光标回读：1431,317
  方式：CoordinateClick（该控件不支持 InvokePattern，改用坐标点击）
```

- 光标回读与目标**完全一致**（`cursorLanded=true`），说明 `SetCursorPos` 真的生效了；
- 截图里「97.09GB」那一行出现了一个选中框 —— 点击确实落在了标签上。

之前验不成是因为**我这个 shell 跑在沙箱里**（`SetCursorPos` 返回错误 5），
而插件跑在 DSH 主进程里，不受那个限制。换句话说：那条"未验证"是**取样环境**的问题，
不是代码的问题。

### 2. Electron：点得动，而且看得见效果

拿 DSH 自己的窗口（`hwnd=2688346`，`Chrome_WidgetWin_1`）当靶子，挑了一个
可逆的按钮：侧边栏折叠。

| 步骤 | 结果 |
|---|---|
| `click_element` `role=Button/name=收起侧边栏` | `InvokePattern` 成功 → 侧边栏真的收起了 |
| 重读树 | 按钮名变成 `打开侧边栏`（`@179,57 42x42`，FOCUSED），侧边栏内容（工作区、会话列表、账号菜单）全部消失 |
| `click_element` `role=Button/name=打开侧边栏` | 成功 → 侧边栏恢复（截图确认） |

按钮改名 + 侧边栏内容从树里消失，是"应用真的响应了"的硬证据 —— 比"返回 ok"强得多。
（两次读都被 `maxNodes` 截断，所以具体数字不能当总数看；判据用的是**内容和名字的变化**。
靶子已复原，没有留下任何状态变化。）

### 3. DPI：这台机器本来就是 150%，不是 100%

`GetDpiForWindow` 读回来是 **144（150%）**，两个靶子窗口都是。
也就是说上面所有点击本来就是在 150% 缩放下做的。同一元素两边的数字：

| 来源 | 「我 的」矩形 |
|---|---|
| 桥（`dryRun` 返回） | `1586,1073 252x84` |
| `dsc.exe tree` | `@(1586,1073 252x84)` |

**逐像素一致**。窗口矩形同理：桥给 `1253,158 666x999`，`list_windows` 给
`1242,113 688x1055`，差的 11px 边框、45px 标题栏正是同一个物理坐标系。
所以"桥声明的 Per-Monitor V2 让坐标和 dsc.exe 对齐"这条在 150% 下是成立的。

#### 125% 也验过了（2026-10-19 晚，用户把缩放改成 125%）

`GetDpiForWindow` = **120（125%）**。四个元素的矩形，桥与 helper（部署副本，Medium）**逐项相同**：

| 元素 | 桥 | `dsc.exe tree` |
|---|---|---|
| 剩余流量 | `1364,272 71x23` | `1364,272 71x23` |
| 充值 | `1656,251 81x40` | `1656,251 81x40` |
| 首 页 | `1318,914 211x70` | `1318,914 211x70` |
| 我 的 | `1528,914 211x70` | `1528,914 211x70` |

在 125% 下按坐标点「剩余流量」→ 落在 (1399,283)，正好是 `1364 + 71/2, 272 + 23/2`，
光标回读一致。所以 125% / 150% 两种缩放都有实测数据。

顺带一个容易骗人的现象：**未声明 DPI 感知的进程读 `GetCursorPos` 会拿到 0.8 倍的值**。
同一个光标位置，helper（Per-Monitor V2）报 `1399,283`，一个裸 PowerShell 报 `1119,226`
—— 1119×1.25 = 1399。差一点就得出"鼠标没动到正确位置"的错误结论。
（判断光标位置必须用和 helper 同样 DPI 感知的进程，否则量的是虚拟化后的坐标。）

### 4. "语义点击也是真的移动了鼠标"——现在有回读证据

用户问过一句："感觉你在操作的过程中鼠标还是没动啊？就算是语义树也要假装鼠标在动吧？"

结论：**会动，而且两种路径都会**。实测（125% 缩放，两次独立测量）：

| 动作 | 元素矩形 | 点击后光标（回读） |
|---|---|---|
| `role=Text/name=剩余流量`（走坐标点击） | `1364,272 71x23` | `1399,283` = 中心 |
| `role=Button/name=我 的`（走 **InvokePattern**，不点坐标） | `1528,914 211x70` | `1634,949` ≈ 中心 `1633,949` |

语义触发之前会先 `SetCursorPos` 到元素中心，**只是为了让人看见**（Invoke 本身不需要坐标）。

之前为什么看起来"没动"，有两个原因，都修了或澄清了：

1. **报告里没证据**：`click_element` 只打印一句"光标已移到元素上"，没有坐标、没有回读，
   谁也无法据此确认。现在 helper 移完会**回读 `GetCursorPos`**，返回
   `cursorMoved`（回读验证过）、`cursorAfter`，插件文本带上坐标：
   `光标已移到元素上（回读 1634,949）`；没到位时明确写"光标没能落到元素中心
   （回读 x,y）—— 只影响看得见，触发已经发出"。
2. **沙箱里的测试确实动不了**：`tools/` 下的脚本跑在工作区（Low 完整性），
   `SetCursorPos` 被拒（错误 5），所以跑矩阵测试时窗口在动、光标确实不动。
   那是环境限制，不是实现省略。

顺带说明一条**不做**的事：语义点击绝不会补一次真实鼠标按下/抬起 ——
那会让同一个按钮触发两次（这个坑本项目踩过，见 CmdClickElement 里的注释）。
"让人看见"只到"光标移过去"为止。

### 附带：同一分钟内"来回翻转"——查清了，是 maxNodes 伪影

同样是 DSH 那个窗口，几分钟内三次读，当时看着像 helper 间歇性失明：

| 时刻 | 那次读的结果 | 当时的 maxNodes |
|---|---|---|
| 第一次 | **helper 直读**成功，60 个（带 `aid=`、`Secondary Actions`） | 60 |
| 第二次 | helper "不可用" → **桥**接手，397 个 | **8** |
| 第三次（两次点击之后） | helper 又读得动 | 60 / 24 |

**不是 helper 翻转，是我自己的取样太小。** helper 的 `named`/`actionable` 只数
**列出的**那些节点（`BoundedWalk` 在 `maxNodes` 处就停），而 `usable` 的判据是
`actionable>=5 || named>=12`。于是 `maxNodes=8` 时前 8 个节点恰好全是 `Pane`
→ `named=0` → 判定"树不可用" → 插件去换桥，还对模型说"这棵树信息很少，改用截图"。

也就是说：**"这棵树有没有信息量"变成了 maxNodes 的函数。**

这是 28e90d7 那个 bug 的镜像位置 —— 那次修的是**桥**（它数的是自己印出来的行数），
helper 这边同一处没跟上。修法：helper 增加 `countsConclusive`，
只有"走满上限但样本仍然 ≥64 行"时才敢下结论；样本不足时 `usable` 报 true
（不知道就不说它差），插件改说"样本不足，把 maxNodes 调大再看"，也不再去换桥。

回归测试：`tools/test-tree-sample.mjs`（自己造一个 12 个 Label 的窗口，
`maxNodes=2` 时不许判"不可用"、也不许说"信息很少"；`maxNodes=200` 时结论照旧）。

### 还剩下的那半：谁启动 dsc.exe

| 启动链 | `dsc.exe tree 66988` | 桥 |
|---|---|---|
| `explorer → DSH → dsc.exe`（插件的**部署副本**） | **40 个元素** | —（用不到） |
| `explorer → DSH → bash → powershell → dsc.exe`（仓库副本） | **7 个元素** | 39 个 |

这一条**当时归因给"启动链"，后来查清是错的**：真正的变量是**进程的完整性级别**。
同一份 exe，在 `D:\dsh workspace`（带 `Low Mandatory Level` 标签）里执行 →
Low → 7 个节点；复制到 `%TEMP%` → Medium → 20~40 个节点。
链条长度、中间有没有 .NET、是不是 DSH 拉起来的，全都不影响。

详见 `定案-低完整性级别不是Chromium的毛病.md`。

实用结论因此要改一处：**桥不是"绕开 .NET"，而是"绕开出问题的执行位置"**
（python → PowerShell 都在工作区外，因此是 Medium）。插件用安装目录里的 helper
本来就不受影响。

## 还没验的（别当成已经成立）

1. **125% 缩放没验。** 150% 已验（见上），125% 的机制相同但没数据；
   换缩放需要动系统设置，没有擅自改。
2. **只点过 Tauri（SuYou）和 Electron（DSH 自己）。** WebView2 没点过；
   微信能读到树但没点过。
3. **`visual-candidates`（截图选候选）仍然只有设计，没有实现。**
4. **换机器未测**（一直挂着的 B 项）。
