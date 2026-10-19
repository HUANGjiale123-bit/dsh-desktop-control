# 第九轮：Chromium 读不到 —— 排查到哪一步了

> 2026-10-19。这份不写"发现了什么"，写**边界在哪**：确认的事实、排除的假设、还没走的路。

---

## 一、确认的事实

### 1. 树存在，而且能读到

**PowerShell 从 bash 启动，读 SuYou 的 `Chrome_RenderWidgetHostHWND` 拿到 29 个节点**，包括 `Document`、`Text`（首页、规则模式、全局模式）、`RadioButton`、`Button`（充值）、`Image`，**都有名字、控制类型和精确矩形**。

**这不是推测，是可重复的实验。**

### 2. 同一窗口、同一秒，两个进程读到不同结果

| 属性 | bash 启动的 PowerShell | .NET 父进程启动的 PowerShell |
|---|---|---|
| `ClassName` / `ProcessId` / `NativeWindowHandle` | 相同 | 相同 |
| `ProviderDescription` | 空 | 空 |
| **`IsControlElement`** | **False** | **True** |
| **`IsContentElement`** | **False** | **True** |
| **`Children`** | **1** | **0** |
| **`Descendants`** | **29** | **0** |

**只有这两个布尔属性不同，而它们正好解释节点数。**

### 3. 六个父进程的分界线

| 父进程 | 是 .NET | 节点数 |
|---|---|---|
| `bash` | 否 | 39 / 38 / 37 / 37 / 29 ✓ |
| `cmd.exe` | 否 | 39 / 38 / 37 / 37 / 29 ✓ |
| `python.exe` | 否 | 39 / 38 / 37 / 37 / 29 ✓ |
| `dsc.exe` | 是 | 5 / 4 / 3 / 2 / -1 ✗ |
| `DpiTest.exe` | 是 | 5 ✗ |
| `JobTest.exe` | 是 | 5 ✗ |

### 4. 影响会沿着整条进程树传下去

```
.NET → cmd.exe → powershell.exe        → 5
.NET → python.exe → powershell.exe     → 5
```

**中间夹一层非 .NET 的进程也没用。**

**但 `python.exe` 从 bash 启动时，它启动的 PowerShell 能读到 39。**

**所以不是"父进程"这一个变量，是"整条链上有没有 .NET"。**

---

## 二、排除的假设（22 项）

| # | 假设 | 怎么排除 | 结果 |
|---|---|---|---|
| ① | 位数不同 | 读 PE 头 | 两个都是 x64 |
| ② | 入口点映射错 | 显式 `SendMessageW` | 仍 0 |
| ③ | UIA 未初始化 | `RootElement` warm-up | 成功，仍 0 |
| ④ | 枚举出错 | 硬编码句柄 | 仍 0 |
| ⑤ | 句柄不对 | 两边打印 | 完全一致 |
| ⑥ | 线程 apartment | `[STAThread]` | 仍 0 |
| ⑦ | 缺 WinForms | `EnableVisualStyles` + `Form` | 仍 0 |
| ⑧ | 缺 OLE | `OleInitialize` 返回 S_OK | 仍 0 |
| ⑨ | COM 安全 | `CoInitializeSecurity` 返回 S_OK | 仍 0 |
| ⑩ | DPI 感知 | 调与不调都是 5 | ✗ |
| ⑪ | `-NonInteractive` | 去掉 | 仍 5 |
| ⑫ | `CreateNoWindow` | 关掉 | 仍 5 |
| ⑬ | stdout 重定向 | 继承也是 5 | ✗ |
| ⑭ | job object | `python.exe` 也在 job 里，能读到 39 | ✗ |
| ⑮ | 会话不同 | 都是 session 9 | ✗ |
| ⑯ | 模块列表 | 84 vs 84，零差异 | ✗ |
| ⑰ | 环境变量 | 73 个，无可疑差异 | ✗ |
| ⑱ | `Accessibility` 未加载 | 显式引用 + MSAA 成功 | 仍 4 |
| ⑲ | `UiaHostProviderFromHwnd` | 服务端 API，给空壳（复核已指出我用错） | ✗ |
| ⑳ | 托管封装问题 | 原生 `IUIAutomation.FindAll` 也是 0 | ✗ |
| ㉑ | `uiautomationcore.dll` 加载 | PowerShell 里显式 `LoadLibrary` 后仍是 29 | ✗ |
| ㉒ | cmd 中间层 | `.NET → cmd → ps` 仍是 5 | ✗ |

---

## 三、剩下的唯一线索

**PowerShell 的 `uiautomationcore.dll` 加载数是 0，我的程序是 1。**

**但显式加载它不改变结果。** 所以这条线索**指向方向但没给出机制**：

```
PowerShell 不加载原生 UIA core → 走的是托管/MSAA 路径 → 读到 29
我的程序加载了原生 UIA core   → 走 COM 路径 → 读到 0
显式加载不影响                → 说明差异在"谁先初始化"，不是"有没有加载"
```

**可能是**：**`AutomationElement` 内部在首次调用时选择实现，而选择依据是某种进程级状态。**

---

## 四、我看到但没验证的线索

**Stack Overflow 上有同一个现象的提问**（`IsControlElement` 该为 true 却为 false）：

> 这可能是微软在 .NET 中的 UI 自动化实现中的一个问题……
> 从我的测试来看，这似乎是 `UIAutomationCore.dll` 内部的一个问题。

**回答里推荐了 `UIAComWrapper`**（TestStack）—— 一个**绕过托管 `UIAutomationClient`** 的替代实现。

**我没试它。** 这是**最值得试的下一条路**，因为它正是"换掉托管封装"的方案，而我的原生实现试错了几次都还没走通（vtable 顺序、GUID 都踩过错）。

---

## 五、还没做的（按价值排序）

```
① 试 UIAComWrapper —— 现成的、专门解决这个问题的库
② 查 AutomationElement 内部怎么选实现（装 .NET 参考源，读 FromHandle）
③ 用 Process Monitor 看两个进程的注册表/文件访问差异
④ 接受现状：Chromium 走视觉回退（已验证能工作）
```

---

## 六、必须写清的边界

**证明了**：

```
✓ Chromium 的 UIA 树在这台机器上能读到（29 个节点，有名字和矩形）
✓ 读不到的原因是 IsControlElement / IsContentElement 在两个进程里不同
✓ 这个差异会沿进程树传递
```

**没证明**：

```
✗ 插件本身能读到（dsc.exe 仍然读不到）
✗ 这是所有 Chromium 应用、所有机器的普遍现象
✗ 根因在 .NET 运行时的哪个具体机制
✗ UIAComWrapper 能否解决（没试）
```

---

## 七、现在能交付的

**坐标契约（已完成，独立于这条线）**：

```
space:"normalized" —— 给 0..1 的比例，不依赖图片尺寸
11 项几何测试 + 5 项端到端测试全过
```

**这条线不影响坐标契约的可用性** —— 视觉回退本来就工作，而 normalized 让视觉定位不再需要知道图片尺寸。
