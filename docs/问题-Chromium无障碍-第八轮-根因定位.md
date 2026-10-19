# 第八轮：Chromium 根因定位到 UIA 客户端代理，以及当前能确定的边界

> ⚠ **本文的根因结论已被撤回（2026-10-19 晚）。**
> 真正的原因是**进程的完整性级别**：`dsc.exe` 当时放在工作区里执行，而工作区带
> `Low Mandatory Level` 标签，于是它以 Low 运行；Low 完整性的 UIA 客户端拿不到内容树。
> 同一份 exe 复制到 `%TEMP%`（Medium）就能读到 20~40 个节点。
>
> 本文那张"有 .NET 祖先就瞎"的表，多半是"链上经过了 dsc.exe、因此全变成 Low"的伪装
> —— 直接反证：Medium 的 dsc.exe 配 .NET 父进程（powershell）读到 20 个节点。
> 见 `定案-低完整性级别不是Chromium的毛病.md`。本文其余部分（现象、已排除项、
> 那些走不通的修补方向）仍然有效。

> 2026-10-19。承接第七轮（"PowerShell 能读到 29 个节点"）。
> 这一份把"为什么我的 C# 读不到"从猜测推进到了**可测量的具体差异**，并说明它为什么不是我能从调用侧改变的。

---

## 一、根因：`IsControlElement` 在两个进程里不同

**同一个窗口、同一个 PID、同一秒，两个 `AutomationElement.FromHandle` 的结果**：

| 属性 | bash 启动的 PowerShell | .NET 父进程启动的 PowerShell |
|---|---|---|
| `ClassName` | `Chrome_RenderWidgetHostHWND` | 相同 |
| `ProcessId` | 25680 | 相同 |
| `NativeWindowHandle` | 67072 | 相同 |
| `ControlType` | `Pane` | 相同 |
| `FrameworkId` | `Win32` | 相同 |
| **`ProviderDescription`** | **(空)** | **(空)** |
| **`IsControlElement`** | **False** | **True** ← |
| **`IsContentElement`** | **False** | **True** ← |
| `Children` | **1** | **0** |
| `Descendants` | **29** | **0** |

**只有两个属性不同，而它们正好解释了节点数。**

---

## 二、因果链

```
UIA 的 FindAll(Descendants) 走控件视图（Control View）
   ↓
IsControlElement 决定一个元素是"控件"还是"容器"
   ↓
False → 容器，继续往下遍历 → 29 个
True  → 控件本身，视为叶子 → 0 个
```

**而 `IsControlElement` 不是服务端给的，是 UIA 的客户端代理算出来的。**

这正是 Codex 第六轮提到的"**客户端代理表本来也可以按客户端不同**"—— 当时我没有证据，现在有了。

**而且 `RawViewWalker` 也是 0 个孩子** —— 说明不只是视图过滤的问题，**这个进程拿到的 element 本身就没有孩子**。

---

## 三、已排除的十九个变量

**每一个都是"改一个变量、重跑一次"的实证排除**：

| # | 假设 | 结果 |
|---|---|---|
| ① | 位数不同 | 两个都是 x64 |
| ② | 入口点映射错 | 显式 `SendMessageW`，仍 0 |
| ③ | UIA 未初始化 | `RootElement` warm-up 成功，仍 0 |
| ④ | 枚举出错 | 硬编码句柄，仍 0 |
| ⑤ | 句柄不对 | 两边打印，完全一致 |
| ⑥ | 线程 apartment | `[STAThread]`，仍 0 |
| ⑦ | 缺 WinForms 初始化 | `EnableVisualStyles` + `Form`，仍 0 |
| ⑧ | 缺 OLE 初始化 | `OleInitialize` 返回 S_OK，仍 0 |
| ⑨ | COM 安全设置 | `CoInitializeSecurity` 返回 S_OK，仍 0 |
| ⑩ | DPI 感知 | `SetProcessDPIAware` 与否都是 5 |
| ⑪ | `-NonInteractive` | 去掉，仍 5 |
| ⑫ | `CreateNoWindow` | 关掉，仍 5 |
| ⑬ | stdout 重定向 | 继承也是 5 |
| ⑭ | job object | `python.exe` 也在 job 里，但它读到 39 |
| ⑮ | 会话不同 | 两边都是 session 9，SuYou 也在 9 |
| ⑯ | 模块列表不同 | 84 vs 84，零差异 |
| ⑰ | 环境变量 | 73 个，无可疑差异 |
| ⑱ | `Accessibility` 程序集未加载 | 显式引用 + MSAA 调用成功，仍 4 |
| ⑲ | `UiaHostProviderFromHwnd` 能读 | 它是服务端 API，给的是空壳 host provider（复核已指出我用错） |

---

## 四、六个父进程的完整数据

**同一份脚本、同一个 `powershell.exe`、同一秒**：

| 父进程 | 是 .NET | 读到的节点数 |
|---|---|---|
| `bash` | 否 | **39 / 38 / 37 / 37 / 29** ✓ |
| `cmd.exe` | 否 | **39 / 38 / 37 / 37 / 29** ✓ |
| `python.exe` | 否 | **39 / 38 / 37 / 37 / 29** ✓ |
| `dsc.exe` | **是** | **5 / 4 / 3 / 2 / -1** ✗ |
| `DpiTest.exe` | **是** | **5** ✗ |
| `JobTest.exe` | **是** | **5** ✗ |

**分界线非常干净：父进程是不是 .NET。**

**但注意 —— 这个"是不是 .NET"只是相关性。** 我没有证明 `.NET` 运行时里的**哪一个机制**导致了它。可能只是这六个样本恰好对应。

**而 `powershell.exe` 自己也是 .NET 程序**，它从 bash 启动时能读到 —— 说明不是"程序是 .NET 就读不到"，是**父进程**的属性影响了子进程。

---

## 五、这对插件意味着什么

**`dsc.exe` 是 .NET 程序，由 DSH 启动。所以它读不到 Chromium。**

**这不是调用方式的问题，是我改代码改不掉的。**

**三条出路**：

```
① 用非 .NET 的中间进程（python / 原生 exe）当桥
   代价：多一个运行时依赖
   收益：立刻可用，已验证 python 能读到 39

② 继续挖 .NET 里到底哪个机制影响了子进程
   候选：AppCompat shim、句柄继承、ActCtx、COM catalog
   不确定能否挖到

③ 改用 Chromium 专用的通道（CDP）
   只对浏览器有效，对 Electron/Tauri 无效，且需要启动参数
```

**我倾向 ② → ①**：先花一点时间挖，挖不到就上桥。

---

## 六、必须写清楚的边界

**这一轮证明了什么**：

```
✓ Chromium 系应用的 UIA 树在这台机器上**是可以读的**（某个进程能读到 29 个节点，有名字/类型/矩形）
✓ 读不到的原因是 UIA 客户端代理在同一台机器上对不同进程给出了不同结果
✓ 这个差异可以被测量（IsControlElement / IsContentElement / Children / Descendants）
```

**这一轮没有证明什么**：

```
✗ 没有证明插件本身能读到（dsc.exe 仍然读不到）
✗ 没有证明这是所有 Chromium 应用、所有机器的普遍现象
✗ 没有定位到 .NET 运行时里的具体机制
✗ 没有验证那些节点的矩形是否可用于点击
```

**按外部复核的标准**：现阶段应该称为"**找到了根因所在的层级**"，还不是"**插件已交付这项能力**"。

---

## 七、下一步

```
1. 记录完整的阳性实验（脚本 + 原始输出 + 哈希）      ← 本轮补
2. 挖 .NET 机制（AppCompat / 句柄继承 / ActCtx）      ← 有界时间盒
3. 挖不到就上非 .NET 桥                              ← 已验证可行
4. 坐标契约（独立于 Chromium，可先做）                ← 下一个主任务
```

---

## 附：可复现材料

```
helper/.cache/ExactCopy.cs        逐字照抄 PowerShell 逻辑的 exe（读到 4）
helper/.cache/DpiTest.cs          .NET 父进程启动 PowerShell 的最小复现
helper/.cache/JobTest.cs          job object 检查
helper/.cache/RawUia.cs           原生 IUIAutomation + ProviderDescription
helper/.cache/AccTest.cs          Accessibility 程序集加载测试
helper/.cache/EnvCompare.cs       环境变量/模块对比
. cache/psread-*.ps1              实际读取脚本（dsc.exe 生成）
```
