# Electron / Chromium 的元素读取：探测结果与实测记录

> 2026-10-18。起因是我在项目总结里写了一句「Electron / WinUI 内部完全不暴露，
> 只能按坐标点」。**那句话是错的** —— 但正确的版本也不是「MSAA 就能解决」。
>
> 这份文档记录**探测工具、实测数据、以及四种完全不同的失败**。

---

## 一、我原来错在哪

**原话**：

> Electron / WinUI 内部完全不暴露 —— 只能坐标，而坐标是估的
> （实测：网易云 8 个节点、Chrome Legacy Window 一层）

**错在三处**：

| 我写的 | 实际 |
|---|---|
| "Chrome Legacy Window 一层" | 它是**中间窗口的名字**，不代表只有一个节点。Chromium 的 `LegacyRenderWidgetHostHWND::OnGetObject` **同时处理 UIA 和 MSAA**，`OBJID_CLIENT` 会取到内容根 |
| "完全不暴露" | Chrome 138 起 Windows 上默认启用**原生 UIA**；之前经 MSAA 转换层。Electron 检测到辅助技术时会**自动启用**无障碍 |
| "只能坐标" | 取决于**目标的内核版本和渲染架构**，必须逐个探测 —— 而探测结果有好几种，不能糊成一句 |

**来源**：
- [Chrome 官方：Windows UIA 支持](https://developer.chrome.com/blog/windows-uia-support-update)
- [Electron：Accessibility](https://www.electronjs.org/docs/latest/tutorial/accessibility)
- [Chromium `legacy_render_widget_host_win.cc`](https://github.com/chromium/chromium/blob/main/content/browser/renderer_host/legacy_render_widget_host_win.cc)

---

## 二、探测工具

```bash
# 需要白名单（读取界面内容同样要授权）
DSC_ALLOWED_APPS="msedge.exe" dsc.exe a11y_probe <hwnd>
```

**它只读**：不执行输入动作、不改目标设置、不注入 DLL。

**输出字段**：

```
uia.nodeCounts            三次查询的节点数（间隔 120 / 600 / 1500ms）
uia.firstDescendants      Raw View 走前 8 个节点
msaa.tried               每个候选 HWND 的 hr / 节点数 / 矩形数
msaa.chosenHwnd          选中哪个（按"有矩形的数量"评分，不是按节点数）
msaa.ia2                  IA2 是否可用 + HRESULT
handshake.compatHr        AccessibleObjectFromWindow(hwnd, 1) 的 HRESULT
handshake.msaaRootHr      OBJID_CLIENT 根查询
childWindowClasses        子窗口类名（找不到内容宿主时，这是唯一能说明原因的）
coverage                  full / partial / none_or_shell_only
```

---

## 三、实测数据（2026-10-18）

### Edge 155.0.4283.62

```
类名          Chrome_WidgetWin_1
子窗口        ['Intermediate D3D Window']      ← 只有一个
内容 HWND     0
UIA 节点      [2]                              ← Window + Pane
MSAA 根       hr=0x00000000（成功）
MSAA 节点     3000（撞预算）
MSAA 矩形     2
role 分布     10:1  9:1  2:1  1:1  43:36
              ↑CLIENT ↑WINDOW ↑MENUBAR ↑TITLEBAR ↑PUSHBUTTON×36
```

**那 36 个 `role=43` 全部叫「输入法」** —— 是 IME 的重复子元素，把 3000 的预算吃光了。

**有效元素：0 个。**

### SuYou 0.x（Tauri / WebView2）

```
类名          Tauri Window
子窗口        ['WRY_WEBVIEW', 'Chrome_WidgetWin_0', 'Chrome_WidgetWin_1',
              'Chrome_RenderWidgetHostHWND', 'Intermediate D3D Window']
              ↑ 有内容宿主
UIA 节点      [6]
MSAA 节点     3000
MSAA 矩形     71
```

**看起来有 71 个矩形，但**：

```
全部指向同一块区域 [1089, 142, 666, 999]
role 全是 9(WINDOW) / 10(CLIENT)
没有 button / link / text
```

**内容窗口（196820）单独探测：1 个节点** —— 树根本没建。

**有效元素：0 个。**

---

## 四、四种完全不同的失败

**这是这次最重要的收获** —— 以前全都糊成「没有元素」：

| 情况 | 特征 | 怎么办 |
|---|---|---|
| **① 没有内容 HWND** | `childWindowClasses` 里没有 Chromium 宿主 | 该轮子可能用 DComp 无窗口渲染，MSAA 只能摸外壳 |
| **② 有内容 HWND 但没树** | 内容窗口节点数 ≤ 1 | 需要辅助技术激活信号 |
| **③ 有树但全是容器** | 矩形数很多但 role 全是 9/10 | 树是外壳，不是内容 |
| **④ 有细粒度元素** | 有 button/link/text + 各自不同的矩形 | **可以接** |

**Edge 是 ①，SuYou 是 ③。**

---

## 五、为什么两个简单握手都不够

```
AccessibleObjectFromWindow(hwnd, 1)   → 0x80004005 (E_FAIL)
OBJID_CLIENT 根查询                    → 成功，但只给外壳
```

**Codex 明确说过**：

> 对已确认的 Chromium 内容 HWND，也可以把 `AccessibleObjectFromWindow(hwnd, 1, ...)`
> 作为**版本相关的兼容探测**；不能当作 Windows 通用开关，更不能据返回失败就认定
> 握手未触发 —— Chromium 该分支可以处理请求但不返回对象。

**所以 E_FAIL 不算证据。** 真正的握手是：

```
① SetWinEventHook（WINEVENT_OUTOFCONTEXT，不注入 DLL）
② 收到 Chromium 的 EVENT_SYSTEM_ALERT → AccessibleObjectFromEvent 响应
③ 重新请求 OBJID_CLIENT 内容树
```

**这条我还没实现。** 而且 Edge 那种「没有内容 HWND」的情况，第 ② 步不知道挂在哪。

---

## 六、一个必须说清的限制

**即使全部打通，也不可能拿到所有元素。** 以下三条任何时候都成立：

```
· canvas / WebGL 自绘内容 —— 没有 DOM 元素可暴露
· 没写 ARIA、没写名字的图标按钮 —— 树里有，但你不知道它是什么
· 主动关闭可访问性的应用 —— 它就是不给你
```

**所以交付目标应该写成**（Codex 的原话）：

> 不改应用的前提下，尽可能读取它已暴露的可交互语义，并报告覆盖与失败原因。

**这比「Electron 只能坐标」准确，也比「有了 MSAA 就能列出所有按钮」可靠。**

---

## 七、顺带修掉的两个实现错误

**这两个都是跑出来的，不是想出来的。**

### ① BFS → DFS

第一版用队列（BFS）。Edge 上 400 个节点只有 3 个矩形 —— 因为 Chromium 的树**深而窄**（根 → document → 几层无名容器 → 真正的元素），BFS 把预算全花在同一层的容器上。

改成栈（DFS），预算 400 → 3000。

### ② sample 取"有用的"而不是"前 40 个"

SuYou 有 71 个矩形，但前 40 个节点里只有 2 个 —— 因为 DFS 顺序下浅层的是容器。

改成**优先取有矩形的**，再按「有默认动作 / 有名字」排序。

**两处都是"我认为合理"和"实际跑出来"的差别。**

---

## 八、握手试过了，不奏效

完整实现了外部复核描述的那套：

```
SetWinEventHook(WINEVENT_OUTOFCONTEXT，绑目标 PID)
  + PeekMessage 泵消息 700ms
  + 收到 EVENT_SYSTEM_ALERT(idObject=1) 时 AccessibleObjectFromEvent 响应
  + 重新取树
```

**两个目标都收到 0 个事件，握手前后树大小完全没变。**
FindAll 和 TreeWalker 结果也一致，所以不是遍历方式的问题。

诚实结论：**模拟 AT 客户端不足以让 Chromium 建树。**

问题已整理成 docs/问题-Chromium无障碍拿不到元素.md，交给外部判断哪条路还有希望。

## 九、下一步

```
① 实现 SetWinEventHook 握手（helper 里建消息循环 + 超时）
② 试能不能让 Edge 的 Pane 展开子树
③ 如果内容窗口能建树，接进 desktop_read_window 作为 backend=msaa
④ 完整遍历要有预算、错误隔离、provider 卡死的硬超时
```

**改 `server/toolset.mjs` 时注意**：重试留在同一次 helper 调用内，别每一步都让模型思考一轮。
