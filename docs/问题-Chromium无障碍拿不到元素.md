# 问题：无注入前提下，拿不到 Chromium/Electron 的可交互元素

> 写给愿意看细节的读者。我需要的是**判断哪条路还有希望**，不是"继续试"。

---

## 环境与约束（都是硬约束，不能放宽）

```
Windows 11 10.0.26200
辅助进程 dsc.exe：C# / .NET Framework 4.8 / PerMonitorV2 DPI aware
约束一：不能向目标进程注入任何 DLL
约束二：不能给目标应用加启动参数
约束三：不能装浏览器扩展、不能用 CDP
约束四：不能要求用户安装屏幕阅读器
```

**用途**：让一个模型能"看见"并操作 Windows 桌面上的应用。拿不到元素时只能按坐标点，而坐标是估的。

---

## 实测数据（全部来自真实运行，不是文档引用）

### 目标 A：Microsoft Edge 155.0.4283.62

```
窗口类名        Chrome_WidgetWin_1
EnumChildWindows 结果：
                ['Intermediate D3D Window']      ← 只有一个
内容 HWND      无

UIA:
  AutomationElement.FromHandle(hwnd) 可行
  FindAll(TreeScope.Descendants, Condition.TrueCondition) → 2 个
  TreeWalker.RawViewWalker 逐层递归 → 2 个（1 个有矩形）
  前两个后代：
    [ControlType.Window] '大肥鱼-哔哩哔哩_bilibili - Microsoft Edge' rect=[0,0,2226,1329]
    [ControlType.Pane]   ''                                        rect=[11,0,2204,1318]

MSAA:
  AccessibleObjectFromWindow(hwnd, OBJID_CLIENT) → hr=0x00000000（成功）
  有界 DFS 遍历 3000 个节点（撞预算）
  其中有有效矩形的：2 个
  role 分布：10(CLIENT):1  9(WINDOW):1  2(MENUBAR):1  1(TITLEBAR):1  43(PUSHBUTTON):36
             ↑ 那 36 个全部叫「输入法」，是 IME 的重复子元素
```

**结论：拿到的全是浏览器外壳，一个网页元素都没有。**

### 目标 B：SuYou（Tauri / WebView2）

```
窗口类名        Tauri Window
EnumChildWindows 结果：
                ['WRY_WEBVIEW', 'Chrome_WidgetWin_0', 'Chrome_WidgetWin_1',
                 'Chrome_RenderWidgetHostHWND', 'Intermediate D3D Window']

UIA:
  FindAll → 6 个
  TreeWalker → 6 个（5 个有矩形）
  后代类型：Window / Pane / Pane / Pane('速游') / Pane('Chrome Legacy Window')

MSAA:
  顶层窗口：3000 节点 / 71 个有矩形
  但 71 个矩形的坐标**全部相同**：[1089, 142, 666, 999]（整个内容区）
  role 全是 9(WINDOW) / 10(CLIENT)
  内容窗口 Chrome_RenderWidgetHostHWND 单独探测：**1 个节点**
```

**结论：有结构、有矩形，但没有一个细粒度元素。**

---

## 已经试过的（都无效）

| 试过的东西 | 结果 |
|---|---|
| `AccessibleObjectFromWindow(hwnd, 1, IID_IAccessible)` | `E_FAIL` (0x80004005) |
| `SetWinEventHook(EVENT_MIN, EVENT_MAX, pid, OUTOFCONTEXT \| SKIPOWNPROCESS)` + `PeekMessage` 泵 700ms | **hook 安装成功，收到 0 个事件** |
| 收到 `EVENT_SYSTEM_ALERT` 时调 `AccessibleObjectFromEvent` 响应 | 没机会执行（0 事件） |
| `FindAll(Descendants)` vs `TreeWalker` 逐层递归 | 两者结果完全一致（2 / 2、6 / 6） |
| 握手前后对比 MSAA 树大小 | 3000/71 → 3000/71，无变化 |
| 给内容窗口单独做同样的查询 | 1 个节点 |

**注意**：`SetWinEventHook` 用的是 `WINEVENT_OUTOFCONTEXT`，**不注入 DLL**；钩子绑定了目标 PID，不是全系统。

---

## 我的假设（可能是错的）

1. Chromium 的"检测到辅助技术"需要的是**真正的 AT 客户端注册**（像 NVDA 启动时向系统声明自己是读屏器），一个临时进程挂事件钩子**不在它的判定范围内**。
2. Edge 155 用 DirectComposition 渲染（`Intermediate D3D Window`），网页内容**没有独立的 HWND**，所以 `Chrome_RenderWidgetHostHWND` 这条路对它不适用。
3. 托管 `AutomationElement` 和原生 `IUIAutomation` 可能在触发 AT 检测上没有区别 —— 但**我不确定**，所以下面问了。

---

## 我想问的具体问题

**Q1. 触发信号**

一个非注入的外部进程，在不装屏幕阅读器的前提下，**有没有可靠办法**让 Chromium 建立内容无障碍树？如果有，它检测的到底是**什么信号**（窗口消息？注册表？COM 接口调用？进程名白名单？）

**Q2. Edge 的 DComp 渲染**

`Intermediate D3D Window` 存在时，网页内容的无障碍树挂在哪里？

- 还在 `Chrome_WidgetWin_1` 的 `OBJID_CLIENT` 上，只是没建？
- 还是挂在**另一个进程**（渲染进程 / 合成器）的某个窗口上？
- 有没有办法枚举出来？

**Q3. `AccessibleObjectFromWindow(hwnd, 1)` 的语义**

我调用后拿到 `E_FAIL`。官方/源码里这个分支到底做什么？

- 它是否会**处理请求但不返回对象**？
- 调用一次和调用多次有区别吗？
- 需要配合什么才有意义？

**Q4. SuYou 那种"有内容 HWND 但只有 1 个节点"**

`Chrome_RenderWidgetHostHWND` 返回 1 个节点，是：

- 正常的"树还没建"？
- 还是"这个 HWND 不该直接取 `OBJID_CLIENT`，要走父窗口"？
- 或者 WebView2 和 Chrome 的行为不同？

**Q5. 托管 vs 原生**

`AutomationElement`（UIAutomationClient 托管封装）和直接用 `IUIAutomation` COM 接口，在**触发 provider 建树**这件事上有没有区别？

**Q6. 如果以上都不行**

在"不注入、不加启动参数、不装读屏器"这三条都不放宽的前提下，**是不是就只能按坐标点了**？

如果是，**有没有办法至少拿到"粗略的语义"**（比如：这块区域是个视频播放器、那块是搜索框），而不是纯像素？

---

## 附：已有的实现（如果需要看代码）

```
helper/src/Msaa.cs             MSAA 入口（OpenClient / ReadNode / ChildrenBatch / ProbeIA2）
helper/src/A11yHandshake.cs    SetWinEventHook + 泵消息 + AccessibleObjectFromEvent
helper/src/Probe.cs            a11y_probe 命令：UIA + MSAA + 握手 + 覆盖率判断
```

全部 C# 5（.NET Framework 4.8 的老 csc），**只读，不执行任何输入动作**。
