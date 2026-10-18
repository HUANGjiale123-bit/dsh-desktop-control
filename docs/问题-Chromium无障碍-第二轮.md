# 问题（第二轮）：Chromium 的 UIA provider 根本不挂，遍历器已修好

> 对应上一轮的《问题-Chromium无障碍拿不到元素.md》。
> 对方的复核说得对：旧探测器有 bug，**所以上一轮的结论不能用**。
> 这份记录修好之后的重测结果，以及新的卡点。

---

## 一、先认错：上一轮的数字全是错的

对方用纯内存复现了 `WalkBounded`，抓到三个确定的错误。**全部属实，已修。**

| 模拟输入 | 应该 | 旧实现输出 | 截断报告 |
|---|---|---|---|
| 1 父 + 2 个简单子元素 | 3 | **3000** | node_budget |
| 1 父 + 70 个完整对象孩子 | 71 | **65** | **无** |
| 一个对象把自己列为孩子 | 1 | **41** | depth_budget |

**根因**：简单子元素的 `Owner` 是父对象，旧代码无条件枚举 `cur.Owner` 的孩子 —— 等于把父对象的孩子当成这个孩子的后代，**无限递归**。

**这直接解释了**"36 个重复输入法"和"71 个同矩形" —— 那些不是应用的行为，是我的 bug。

### 修好之后

```
BoundedWalk.cs   纯逻辑，不碰 COM
WalkSelfTest.cs  11 项自测（对方给的四条 + 深度上限），不需要任何真实应用
```

**自测还抓到我自己的第四个 bug**：`UniqueCount` 是普通字段、在函数末尾赋值，`node_budget` 提前返回时永远是 0（走满了 10 个节点却报"节点数 = 0"）。改成属性。

**重测同一批窗口**：

```
SuYou   3000 节点 / 71 矩形 / 撞预算   →  116 节点 / 12 矩形 / 走完 / 重复边 0
Edge    3000 节点 /  2 矩形 / 撞预算   →   24 节点 /  3 矩形 / 走完 / 重复边 0
```

**差了一个数量级以上。上一轮基于这些数字的结论全部作废。**

---

## 二、对方其余几条也已修

| 问题 | 修法 |
|---|---|
| `if (n > 0) break`，三轮重查实际只跑一轮 | 固定跑三轮，**每轮重新取根**，记录每轮的根名/类名/节点数/矩形数/provider |
| 选中候选后把 `truncReason` 设成 null | 保留每个候选的截断原因，并新增 `traversalComplete` / `duplicateEdges` / `contentStatus` |
| 只对顶层 PID 挂事件钩子 | 每个候选记录自己的 HWND / 类名 / PID |
| 用矩形数量选"最佳根" | 候选记录保留；评分逻辑仍在，但每个候选的数据都留下了 |
| `_responded` 跨调用不重置 | **还没修**（见第四节） |
| `ChildrenBatch` 没有用 `get_accChild` 区分整数孩子 | **还没做**（见第四节） |

---

## 三、新的卡点：`UiaHasServerSideProvider = False`

按建议加了 `UiaHasServerSideProvider(hwnd)` 诊断。三个目标：

| 目标 | 类名 | UIA 服务端 provider | FrameworkId | MSAA 节点/矩形 | UIA 三轮节点数 |
|---|---|---|---|---|---|
| Edge 155.0.4283.62 | Chrome_WidgetWin_1 | **False** | **Win32** | 24 / 3 | 2, 2, 2 |
| SuYou（Tauri/WebView2） | Tauri Window | **False** | **Win32** | 116 / 12 | 6, 6, 6 |
| DeepSeek Harness 自己 | Chrome_WidgetWin_1 | **False** | **Win32** | 47 / 6 | 3, 3, 3 |

**`FrameworkId = Win32` 说明 UIA 客户端拿到的是系统从 MSAA 转换的代理，不是 Chromium 自己的 provider。**
**`UiaHasServerSideProvider = False` 说明这三个窗口都没有服务端 UIA 实现。**

### 我试过但都没能触发

```
① 读 Name / ClassName（最小属性）
② 读 IsContentElement / IsControlElement / GetSupportedPatterns（内容属性）
③ AccessibleObjectFromWindow(hwnd, 1) 兼容查询
④ SetWinEventHook(OUTOFCONTEXT, 绑目标 PID) + PeekMessage 泵 700ms
⑤ 每轮重新取根，三个时点（0 / 600 / 1500ms）重查
```

**五个手段之后，三轮节点数一次都没变过。**

---

## 四、我想问的

### Q1. `UiaHasServerSideProvider = False` 该怎么解读？

我理解它意味着"这个窗口没有服务端 UIA 实现，UIA 走系统代理"。但：

- 一个**正常启用**了原生 UIA 的 Chrome/Edge 窗口，这个调用应该返回 `True` 吗？
- 返回 `False` 是"应用没实现"，还是"这个 API 在跨进程场景下本来就返回 False"？
- 有没有别的 API 能区分"服务端 provider 存在但没建树"和"根本没有 provider"？

### Q2. Chromium 提升到 `kAXModeBasic` 的确切触发条件

对方上一轮说：

> 网页内容属性使用请求 `kAXModeBasic`

**我读了 `IsContentElement`、`IsControlElement`、`GetSupportedPatterns` —— 都没触发。**

那么，具体是**哪些属性**会触发？

- 是否需要读 `ControlType`（我只读了一次，在旧的 findloop 里）？
- 是否需要读 `LegacyIAccessiblePattern` 的某个属性？
- 是否需要先获得 `IUIAutomationElement` 的原生指针（而不是托管封装）才能让调用真的到达 provider？
- **托管 `AutomationElement` 的调用，会不会在客户端就被代理层拦截、根本到不了 Chromium？**

### Q3. 系统代理的"拦截"发生在哪一层

如果 UIA 客户端调 `FindAll`，而目标是 `Win32` 代理：

- 这个代理是从 **MSAA 树**转换出来的吗？
- 如果是，那么 Chromium 的 MSAA 树**本身**就只有外壳（24 个节点、走完了），是否说明**Chromium 连 MSAA 内容树都没建**？
- 如果是"MSAA 内容树也没建"，那要触发的是**MSAA 侧的激活**，而不是 UIA 侧的？

### Q4. ID-1 兼容查询的正确用法

对方上一轮说：

> `OnScreenReaderHoneyPotQueried` 不再仅凭这一次查询启用内容支持：它还检查**名称属性是否被使用**。

我现在的顺序是：发 ID-1 查询 → 读 `accName`。

**但读的是哪个对象的 `accName`？**

- 顶层窗口的 `accName`（返回的是浏览器标题）？
- 还是必须读 **ID-1 查询返回的对象**？但那次查询返回 `E_FAIL`，没有对象可读。

**这个"名称属性被使用"的检查，读的是谁的名字？**

### Q5. 有没有"不装读屏器"的办法让它进入 AT 模式

在不装 NVDA 的前提下：

- 有没有一个**命令行开关 / 注册表项 / 环境变量**能让 Chromium 认为"有 AT 在用"，**且不算"修改目标应用"**？（比如进程级的、不影响应用的）
- 或者：能不能**启动一个最小的、真正的 UIA 客户端**（不是模拟）来触发它？"真正的客户端"和"模拟的客户端"在 provider 眼里区别在哪？

### Q6. 如果答案还是"不行"

那么按上一轮对方说的：

> 若在有效的访问环境中，UIA 与 MSAA/IA2 确实没有暴露目标内容……那么**不存在还能保证读出真实内部控件列表的通用外部接口**。

我会把 Edge / SuYou 标成"当前接口未取得内容"，交给视觉通路，并保留 `a11y_probe` 作为兼容性报告工具。

**在这种情况下，视觉通路的输出应该长什么样才算"带证据的候选"而不是"模型随口报的坐标"？** 对方上一轮给了方向（`source=vision`、区域、截图标识、"推断"标记），我想要一个具体的字段设计。

---

## 五、还没做的（对方列过，我认）

```
· A11yHandshake 的 _responded 跨调用不重置
· Msaa.ChildrenBatch 用 get_accChild 判断整数孩子是否能得到独立对象
· 内容根的 IA2 单独查（现在只查评分选中的那个）
· 托管 AutomationElement 没有 ProviderDescription（只有原生 IUIAutomationElement 有）
  → 这一版用 FrameworkId 代替，是已知缺口
· 原生 IUIAutomation COM 对照实验（对方说值得做一次受控对照，不值得重写）
```

**Q2 里那个"托管封装会不会在客户端就被拦截"是我最想知道的** —— 如果是，那原生对照实验就值得做；如果两者等价，那就不用浪费这个时间。

---

## 六、当前状态

```
版本      2.18.16
自测      dsc.exe selftest_walk   11 项通过
测试      tools/test-*.mjs        22 个文件全过
自检      tools/selfcheck.sh      32 项全过
探测      dsc.exe a11y_probe <hwnd>（需 DSC_ALLOWED_APPS）
```
