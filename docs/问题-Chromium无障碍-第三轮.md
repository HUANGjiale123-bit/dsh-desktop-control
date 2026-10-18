# 第三轮：阳性对照做完了，结论站得住了

> 对应《给大肥鱼的Chromium第二轮答复-2.18.16.md》。
> 这轮的核心是**加了一个阳性对照**，而它立刻证明我上一轮的两个诊断是错的。

---

## 一、阳性对照

对方说得很准：

> 让同一个实际 helper、相同启动方式查询一个**已知有服务端 UIA 的测试窗口**。
> 若对照也异常，就先查客户端环境；不用安装读屏器。

项目里正好有一个：**`tools/target/bin/dsc-target.exe`**（WinForms，带具名控件）。

### 结果

```
framework     : WinForm          ← 真正的应用 provider
MSAA          : 332 节点 / 38 矩形
UIA           : 32 节点 / 32 矩形（三轮一致）
coverage      : has_candidates
可交互候选    : 12
点查询        : [33] Items / [43] Beta / [9] Input
```

**客户端环境是好的。** 同一个命令能从一个 WinForms 窗口读出真实控件。

---

## 二、它证明我上一轮的两个诊断是错的

### ① `UiaHasServerSideProvider` 证明不了任何事

```
WinForms 窗口（明明有 provider，framework=WinForm）→ UiaHasServerSideProvider = False
```

**上一轮我拿这个 False 当"Edge/SuYou/DSH 都没有 provider"的决定性证据 —— 它什么都不是。**

现在改成三态记录：

```json
{ "status": "returned", "value": false }
{ "status": "exception", "value": null, "exception": "…", "hresult": "0x…" }
```

至少不会再被读成一个干净的真假。

### ② `FrameworkId` 才是有效的区分信号

```
WinForms  → WinForm    ← 应用自己的 provider
Edge 155  → Win32      ← 系统代理
SuYou     → Win32      ← 系统代理
DSH 自己  → Win32      ← 系统代理
```

**这是阳性对照买来的东西** —— 有了对照，Win32 这个值才有意义。

---

## 三、按对方要求修完的其余几项

| 项 | 修法 |
|---|---|
| 枚举失败被当成"正常结束" | `ChildrenBatch` 返回结构化结果（children/obtained/hr/failed/reason），失败传进 `WalkPage.FailReason` |
| 清空 `truncReason`、外壳标成 `full` | 保留真实截断；coverage 改成 `no_content` / `shell_only` / `has_candidates`，单独统计可交互角色 |
| 内容根因矩形少被跳过 | 每个候选独立诊断：根角色、childCount、第一页是对象还是整数孩子、`get_accChild` 能否提升、IA2 |
| 缺有限点查询 | `PointQuery.cs`：`AccessibleObjectFromPoint` + `ElementFromPoint`，记录**目标是否前台**、**点是否真的属于目标** |

### 关于点查询，两个踩到的坑（都是我自己造的）

**坑一**：靶子被全屏 Edge 盖住时，4 个采样点**全部**返回 Edge 的 `Chrome Legacy Window`。
→ 现在结果里带 `targetWasForeground`，并在不是前台时写一条明确警告。

**坑二**：`WindowFromPoint` 返回的常常是**子窗口**（控件），直接和顶层 hwnd 比较会全部判成"不属于目标"。
→ 改用 `GetAncestor(under, GA_ROOT)` 比对。修之前，一个落在 `Beta` 按钮上、读到了 `'Beta'` 的点被报成"不属于目标"。

---

## 四、现在的判据与结论

对方给的分叉条件：

> 点查询得到细元素而根遍历得不到 → 优先修导航入口；
> 两者都只返回系统外壳 → 才进一步查环境和 provider。

| 目标 | 根遍历 | 点查询 | 判定 |
|---|---|---|---|
| **WinForms 对照** | 38 矩形 / 12 可交互 | `Items`、`Beta`、`Input` | **两条路都通** |
| **SuYou (Tauri)** | 12 矩形 / **0 可交互** | **4 个点全是 `Chrome Legacy Window`** | **两条路都只有外壳** |

**SuYou 可以下结论了**：按对方说的措辞，

> 写成"**当前接口未取得内容**"，而不是"已证明应用内部不存在树"。

**Edge 的点查询补了一次，但失败在环境上**：重启 Edge 时它拒绝启动第二个实例
（"Microsoft Edge 未响应，因为现有实例..."），探测到的是那个错误提示窗口。
Edge 的有效数据是更早那次（hwnd=1444552）：framework=Win32、24 节点/3 矩形、
coverage=shell_only、0 个可交互候选 —— **只缺点查询**。

不过判据本身已经被 SuYou 证明能工作（4 个点全部同口径），两者同属 Win32。
所以 Edge 我记成"根遍历为外壳，点查询因环境未补"，而不是"已验证"。

---

## 五、剩下的、以及为什么停在这

```
① Edge 的点查询                 —— 补一次即可（10 分钟）
② 原生 IUIAutomation 对照        —— 对方说"值得做一次受控对照，不值得重写"
                                   我判断收益很低：阳性对照已经证明托管客户端能到达真 provider
③ MSAA 整数孩子的 get_accChild   —— 已实现（候选诊断里有了），只是还没在真实整数孩子的目标上见过效果
④ 视觉通路                       —— 对方给了完整字段设计，是另一个工程
```

**② 我倾向不做**，理由是 **Codex 自己的判据**：

> 原生能读而托管不能：查客户端差异；两者都只有外壳：**停止盲目换属性**。

**阳性对照里托管客户端读到了 38 个矩形和 12 个可交互候选** —— 说明它没有在客户端层面被代理拦死。**那"换原生接口"就不太可能是答案。**

**如果对方认为这个推理有问题，我照做。**

---

## 六、一个我想确认的判断

对方上一轮说：

> **没有可承诺满足这些条件、还能强制所有 Chromium 宿主启用的通用开关。**

而我实测：三个 Chromium 目标（Edge / Tauri / DSH 自己）全部 `framework=Win32`、点查询和根遍历都只有外壳。

**我想确认的是**：

1. 这是**这台机器的环境问题**（比如某种全局的 AT 状态关着），**还是这三个应用的共同行为**？
2. 如果是环境问题，**怎么在"不装读屏器、不改目标"的前提下查出来**？
3. 阳性对照通过了，能不能就此排除"环境问题"？—— **我倾向能排除**，因为同一个进程、同一种调用，WinForms 窗口能读到真控件。**但我想听一个更硬的判据。**

---

## 七、当前状态

```
版本      2.18.17
自测      dsc.exe selftest_walk    11 项通过
测试      tools/test-*.mjs         22 个文件全过
自检      tools/selfcheck.sh       32 项全过
探测      dsc.exe a11y_probe <hwnd>
          → UIA 三轮（每轮记 provider/framework/节点/矩形）
          → MSAA 逐候选诊断（根/childCount/对象vs整数/get_accChild/IA2）
          → 点查询（含前台与归属）
          → coverage: no_content / shell_only / has_candidates
```
