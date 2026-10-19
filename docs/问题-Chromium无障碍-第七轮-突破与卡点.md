# 突破与卡点：Chromium 的 UIA 树能读，但只在 PowerShell 里

> 2026-10-19。这份记录两件事：**一个推翻此前结论的发现**，以及**一个还没解决的差异**。

---

## 一、推翻昨天所有结论的发现

**PowerShell 脚本读 SuYou（Tauri）的子窗口，拿到了完整 UI 树**：

```
=== Chrome_RenderWidgetHostHWND hwnd=67072 ===
  [6]  Document      | 速游          | 666x999
  [10] Text          | 首页          | 60x39
  [12] RadioButton   | 规则模式      | 83x9
  [15] RadioButton   | 全局模式      | 83x9
  [17] Image         | Avatar Sharp  | 49x48
  [19] Text          | 227511        | 74x28
  [22] Text          | 97.18GB       | 83x28
  [23] Button        | 充值          | 97x48
```

**每个元素都有名字、类型、精确矩形。**

**所以昨天写的这些全部作废**：

```
❌ "Chromium 系走系统代理，读不到内容"
❌ "点查询和根遍历都只有外壳"
❌ "无注入前提下这就是死路"
```

**正确说法**：**树是有的，而且能读。** 问题在**我的读取方式**。

---

## 二、同一个窗口、同一时刻，两种结果

| | PowerShell | 我的 C# exe |
|---|---|---|
| `WRY_WEBVIEW` | **38 个节点** | 4 个 |
| `Chrome_WidgetWin_0` | **37** | 3 |
| `Chrome_WidgetWin_1` | **37** | 2 |
| `Chrome_RenderWidgetHostHWND` | **29** | **0** |
| `SendMessage(WM_GETOBJECT, OBJID_CLIENT)` | **49346 / 49343** | **0 / 0** |

**最后一行是关键**：PowerShell 发 `WM_GETOBJECT` 拿到了 `LresultFromObject` 的返回（非 0），我发同样的消息拿到 0 —— **说明目标的 provider 根本没响应我**。

---

## 三、已排除的假设（7 项）

| # | 假设 | 怎么排除的 | 结果 |
|---|---|---|---|
| ① | 位数不同 | 读 PE 头 | 两个都是 x64 |
| ② | 入口点映射错 | 显式 `EntryPoint="SendMessageW"` | 仍 0 |
| ③ | UIA 未初始化 | 先摸 `AutomationElement.RootElement` | warm-up 成功，仍 0 |
| ④ | 枚举出错 | 硬编码 `IntPtr(67072)` | 仍 0 |
| ⑤ | 句柄不对 | 两边打印，逐字对比 | 完全一致（66998/67002/67064/67072/132584） |
| ⑥ | 线程 apartment | `[STAThread]` | 仍 0 |
| ⑦ | 缺 WinForms 初始化 | `EnableVisualStyles()` + `new Form()` | 初始化成功，仍 0 |
| ⑧ | 缺 OLE 初始化 | 显式 `OleInitialize(IntPtr.Zero)` | **hr=0 成功**，仍 0 |

**第 ⑤ 项特别值得记**：我把 PowerShell 的 `wmtest.ps1` 里的 C# 代码**逐字照抄**编译成独立 exe，**结果和脚本里完全不同**。

**同一份代码、同一台机器、同一个 NET Framework 版本** —— 差异只能在**宿主进程**。

---

## 四、已知的宿主差异

**PowerShell 进程加载了 84 个模块**，其中我没有的：

```
Accessibility.ni.dll            .NET 的 Accessibility 程序集
System.Windows.Forms.ni.dll     WinForms
System.Drawing.ni.dll
clbcatq.dll                     COM 目录服务
combase.dll                     COM 基础设施
OLE32.dll / OLEAUT32.dll
RPCRT4.dll
ATL.DLL
```

**我试了显式加载 WinForms（⑦），没用。** 但 `clbcatq.dll`（COM catalog）可疑 —— 它参与 COM 类注册。

---

## 五、我现在认为最可能的原因

**`powershell.exe` 在启动时做了一次 COM 初始化，而那一步让后续的 `WM_GETOBJECT` 能被 Chromium 的 provider 响应。**

**候选**：

1. **`CoInitializeSecurity`** —— 进程级 COM 安全设置，**只能设一次**。PowerShell 可能设了某个宽松的值，而我的是默认
2. **`OleInitialize`** —— 与 `CoInitializeEx` 不同，它额外初始化 OLE 剪贴板/拖放，UIA 的 provider 可能依赖
3. **COM 的 apartment 在更早的时机初始化** —— `[STAThread]` 的初始化发生在 `Main` 之前，但可能在某个依赖 DLL 加载之后

**验证方向**：在 `Main` 最开头显式调 `OleInitialize(IntPtr.Zero)`，再试一次。

---

## 六、能复现的脚本

`.cache/wmtest.ps1`（PowerShell 5.1，能读到 38/29）：

```powershell
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# 枚举子窗口 → 对每个发 SendMessage(WM_GETOBJECT=0x3D, 0, -25) 和 (0, -4)
# → Start-Sleep 1s → FromHandle(h).FindAll(Descendants, TrueCondition)
```

**关键点**：`Add-Type -AssemblyName UIAutomationClient` **在** `Add-Type` 定义 `W` 类**之后**。

---

## 七、下一步（按优先级）

```
① 在 Main 最开头显式 OleInitialize → 重试
② 对比两个进程的 COM apartment 状态（CoGetApartmentType）
③ 试 CoInitializeSecurity 的宽松值
④ 如果都不行：查 powershell.exe 的入口做了什么我们没做的
```

**都不行的话**，还有一个务实选项：**用 PowerShell 脚本作为 helper 的"桥"** —— 让 `dsc.exe` 调用一个 `.ps1`（或 `powershell -Command`）去读树，把 JSON 传回来。

**性能差（每次起一个 PowerShell 进程约 300-800ms）**，但**能立刻拿到数据**，而且**能验证"离开 PowerShell 就不行"这个结论**。

---

## 八、当前状态

```
版本      2.18.21
自测      27 项
测试      22 个文件全过
自检      32 项
探测      dsc.exe a11y_probe <hwnd>（对 Chromium 仍读不到，原因见第三节）
```
