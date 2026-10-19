# 给外部评审：Chromium 无障碍读取的突破与余留问题

> 2026-10-19。这份材料是给**没有看过对话的人**的：项目背景、今天推翻的结论、新发现、以及还没做的事。
> 项目仓库：`D:\dsh workspace\dsh-desktop-control`（DSH 插件 + C# helper `dsc.exe`）。

---

## 一、项目与这个问题从哪来

**目标**：做一个能让模型**观察和操作 Windows 桌面**的插件。安全模型是应用白名单（默认空）+ 会话级授权。

**核心难点**：读界面元素。两条路 ——

```
语义路径：Windows 无障碍 API（UIA / MSAA）→ 拿到元素的名字、类型、矩形、动作能力
视觉路径：截图 → 模型目测坐标 → 点击
```

**视觉路径能工作，但"点得准"依赖模型估坐标。语义路径才是可靠的。**

**卡点**：**Chromium 系应用（Edge、Electron、Tauri/WebView2）读不到内容。**

---

## 二、今天推翻的结论

**昨天我写的结论**（已被今天的实验推翻）：

```
❌ "Chromium 系应用走系统代理，读不到内容"
❌ "根遍历和四个采样点都只有外壳"
❌ "无注入、无参数、不装读屏器的前提下，这就是死路"
```

**推翻它的证据**：用 PowerShell 读 SuYou（Tauri 应用）的子窗口，**拿到了完整 UI 树**：

```
=== Chrome_RenderWidgetHostHWND ===
  Document      | 速游          | 666x999
  Text          | 首页          | 60x39
  RadioButton   | 规则模式      | 83x9
  RadioButton   | 全局模式      | 83x9
  Image         | Avatar Sharp  | 49x48
  Text          | 227511        | 74x28
  Text          | 97.18GB       | 83x28
  Button        | 充值          | 97x48
```

**每个元素都有名字、控制类型、精确矩形。** 这就是语义路径需要的东西。

---

## 三、同一个窗口、同一时刻，两种结果

| | PowerShell | 我的 C# exe |
|---|---|---|
| `WRY_WEBVIEW` | **38 个节点** | 4 |
| `Chrome_WidgetWin_0` | **37** | 3 |
| `Chrome_WidgetWin_1` | **37** | 2 |
| `Chrome_RenderWidgetHostHWND` | **29** | **0** |
| `SendMessage(WM_GETOBJECT, OBJID_CLIENT)` | **49346 / 49343** | **0 / 0** |

**最后一行是关键**：PowerShell 发消息拿到了 `LresultFromObject` 返回（非 0 = 有 provider 响应），我发同样的消息拿到 0。

微软文档说明：**返回 0 时 MSAA 仍会给一个代理接口，但那个代理只提供基础信息** —— 这解释了为什么我只能读到外壳。

---

## 四、排除的九个假设

每一个都是"改一个变量、重跑一次"的实证排除，不是推理：

| # | 假设 | 怎么排除 | 结果 |
|---|---|---|---|
| ① | 位数不同 | 读 PE 头 | 两个都是 x64 |
| ② | 入口点映射错 | 显式 `EntryPoint="SendMessageW"` | 仍 0 |
| ③ | UIA 未初始化 | 先摸 `AutomationElement.RootElement` | warm-up 成功，仍 0 |
| ④ | 枚举出错 | 硬编码 `IntPtr(67072)` | 仍 0 |
| ⑤ | 句柄不对 | 两边打印逐字对比 | 完全一致 |
| ⑥ | 线程 apartment | `[STAThread]` | 仍 0 |
| ⑦ | 缺 WinForms 初始化 | `EnableVisualStyles()` + `new Form()` | 成功，仍 0 |
| ⑧ | 缺 OLE 初始化 | 显式 `OleInitialize` | **返回 S_OK**，仍 0 |
| ⑨ | COM 安全设置 | `CoInitializeSecurity` 最宽松 | **返回 S_OK**，仍 0 |

---

## 五、决定性的两个实验

### 实验 A：逐字照抄 PowerShell 的代码，编译成独立 exe

**结果**：读到 4 个，而不是 38。

**同一份代码、同一台机器、同一个 .NET 运行时。**

### 实验 B：同一份静态编译的 DLL，两个进程分别加载

```csharp
// ProbeDll.dll 里就一个方法：
public static List<string> Read(long hwnd) {
    var el = AutomationElement.FromHandle(new IntPtr(hwnd));
    var found = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
    ...
}
```

| 谁加载它 | 结果 |
|---|---|
| PowerShell：`Assembly.Load(bytes)` 后反射调用 | **count=29**（完整树） |
| 我的 exe：同样的 `Assembly.Load(bytes)` + 反射调用 | **count=0** |

**这排除了：代码写法、程序集形态、加载方式（都是内存加载）。**

**唯一剩下的变量：进程本身。**

---

## 六、相关的两条外部线索

**① 微软博客《IronPython cannot call AutomationElement.FromHandle》**

> `FromHandle` uses `Assembly.GetCallingAssembly` and expects to get a **statically compiled assembly** with references to the correct version of the UIAutomation libraries.

**说明 `FromHandle` 内部确实依赖调用方的程序集身份。** 但实验 B 里两边都是内存加载，这个机制**不能解释 29 vs 0 的差异**。

**② `trycua/cua#1616`（GitHub issue，2026-05 开到现在）**

> Chromium's content tree lives in renderer processes that don't expose UIA on the native HWND. Chromium has an in-process **UIA bridge** but it's gated on an Accessibility Tool requesting the tree.

**评论区有人提到**：发 `WM_GETOBJECT` 带 `UiaRootObjectId` 到 `Chrome_WidgetWin_1` / `Chrome_RenderWidgetHostHWND`，然后轮询到 `Document` 出现。

**还有一条我实测不成立的**：

> Chromium's native occlusion tracking keeps a **fully covered window's web tree empty**.

**实测**：把 SuYou 提到前台、完全无遮挡，结果和遮挡时**一模一样**（0 内容证据）。**这条对本例不成立。**

---

## 七、还有一条没有排除的方向

**我用 `UiaHostProviderFromHwnd` 查了每个子窗口**：

```
WRY_WEBVIEW                    hr=0x00000000  ptr=16100944
Chrome_WidgetWin_0             hr=0x00000000  ptr=16100944
Chrome_WidgetWin_1             hr=0x00000000  ptr=16101504   ← 不同指针
Chrome_RenderWidgetHostHWND    hr=0x00000000  ptr=16100944
```

**全部成功，但拿到的 provider 是空壳**：`Name=''`、`ControlType=-1`、**`QueryInterface` 到 `IRawElementProviderFragment` 失败**。

**也就是说：没有可遍历的原生 provider。** 那 PowerShell 读到的 29 个节点只可能来自 **UIA 的客户端代理层**。

**这条线索指向一个具体问题：同一个代理层，为什么在两个进程里给出的结果不同？**

---

## 八、还没做的事（余留清单）

### A. 坐标契约（Codex 第五轮复核的核心要求，未完成）

**现状问题**：

```
截图文件     1941 × 1242（物理像素，正确）
模型看到的   1680 × 1003（前端缩放后的预览）
```

**工具不知道自己输出的图被缩放成多少**，所以"图像坐标 → 桌面坐标"这一步**仍然留给模型自己猜比例**。

**Codex 的要求**：

> 由代码完成图像坐标到桌面坐标的转换，不再把未知预览比例留给模型猜。

**两条候选路**：

```
A. 工具主动缩放截图（记 {imageWidth, imageHeight, scale, origin}，click 支持 space:'image'）
B. 模型报告它看到的尺寸（便宜，但仍要模型给数字，不满足"由代码完成"）
```

### B. 多条件覆盖（未做）

```
原始图像 / 受控缩小后的定位图像 / 窗口移动 / 窗口改尺寸 /
截图后内容变化 / 被遮挡
```

**现有代码只对窗口几何做校验，对内容变化没有检查。**

### C. 真实操作闭环只做过一次（一个尺寸）

**SuYou 点「我的」tab 成功**，但：

```
· 走的是纯视觉路径（截图估坐标），没用任何无障碍能力
· 只在一个窗口尺寸（688×1055）上验过
· 那张截图恰好没有缩放，所以没验证到缩放情形
```

### D. 原生 UIA 对照（做了一半）

**已完成**：`UiaHostProviderFromHwnd` 探测 + `IRawElementProviderFragment` 的 QI 测试。
**未完成**：如果 QI 成功，应该遍历 `Navigate(FirstChild)` 对比结果。**本例 QI 失败，所以这条走不通。**

### E. 视觉候选方案（未做）

**Codex 给过完整字段设计**（`server/visual-candidates.mjs` + `NativeUiaProbe.cs`），**没实现**。

### F. 另一台机器 / 不同 DPI（未测）

---

## 九、现在倾向的下一步

**结论是"进程差异"，所以有两个务实方向**：

### 方向 1：常驻 PowerShell 桥（快，可立即用）

```
dsc.exe 启动时起一个 powershell.exe 子进程，保持 stdin/stdout
查询：写一行 JSON → 读一行 JSON，约 5-20ms
空闲 60 秒自动退出
```

**优点**：立刻能用，而且它本身就是对"进程假设"的验证。
**缺点**：依赖 PowerShell，不是原生方案。

### 方向 2：继续挖宿主差异

**候选**：
```
· 用 Process Monitor 对比两个进程的注册表/文件访问
· 对比 COM apartment 的实际状态（CoGetApartmentType）
· 检查 powershell.exe 的 AppCompat shim / manifest
· 试把 exe 改名为 powershell.exe（排除兼容性 shim）
```

**不确定能否挖到，但挖到就是原生解法。**

---

## 十、要评审回答的问题

```
① 实验 B（同一份 DLL，两个进程 29 vs 0）还能有什么解释？
   我排除了代码、程序集、加载方式、COM 初始化、线程 apartment。
   还有遗漏的变量吗？

② "Chromium 的 UIA 桥在客户端" 这个说法对不对？
   如果 provider 是 UIA 在客户端合成的，那"哪个进程在问"为什么会改变结果？

③ 常驻 PowerShell 桥 vs 继续挖宿主，哪个更值得先做？

④ 坐标契约那条（A）是不是应该独立于 Chromium 问题先做掉？
```

---

## 附：可复现材料

```
helper/.cache/ExactCopy.cs       逐字照抄 PowerShell 逻辑的 exe（读到 4）
helper/.cache/ProbeDll.dll       静态编译的读取 DLL
helper/.cache/LoadTest.cs        在 exe 里内存加载 ProbeDll 并调用（读到 0）
helper/.cache/loaddll.ps1        在 PowerShell 里内存加载 ProbeDll 并调用（读到 29）
helper/.cache/wmtest.ps1         PowerShell 原始脚本（读到 38/29）
docs/问题-Chromium无障碍-第七轮-突破与卡点.md   本文件的详细版
```
