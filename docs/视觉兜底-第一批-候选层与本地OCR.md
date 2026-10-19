# 视觉兜底：候选层、本地 OCR、两个工具（2.18.38 → 2.18.39）

> 依据外部评审的《轻量视觉兜底实现指引》，**方案改了一处**：OCR 不再打包
> RapidOCR/onnxruntime，改用 **Windows 自带的 `Windows.Media.Ocr`** ——
> "让别人用自己的机器 OCR"。实测零安装、本机 98~290ms。

## 这一批做了什么

| 文件 | 作用 | 有没有接进工具 |
|---|---|---|
| `server/visual-candidates.mjs` | 纯逻辑：PNG 尺寸、归一化裁剪、`viewPointToWindow`、标签归一化、候选构造、候选存储与级联失效 | 未接 |
| `server/ocr-worker.mjs` | OCR 后端：探测语言、对**已登记截图**跑 OCR、行结果 → 原始像素行 | 未接 |
| `server/win-ocr.ps1` | WinRT 调用层（ASCII-only，避免 PS 5.1 把无 BOM 的 UTF-8 当 ANSI） | — |
| `tools/test-visual-candidates.mjs` | 29 项断言（见下） | — |

**没做的**：`desktop_visual_candidates` / `desktop_click_candidate` 两个工具、
`recordObservation` 统一登记、点击前 ROI 复查、局部放大与 model_proposal 工具入口。
这些都还在下一批。

## 实测数据（本机，中文 Windows）

```
OCR 后端：windows-ocr    语言：zh-Hans-CN（系统自带，无需安装）
688x1055 SuYou 截图  → 13 行，98ms
1941x1242 DSH 截图   → 42 行，290ms
```

识别质量与已知短板（都写进了归一化和测试）：

| 现象 | 例子 | 处理 |
|---|---|---|
| 汉字之间插空格 | `我 的` / `剩 余 流 量` | `normalizeLabel` 去掉所有空白再匹配 |
| 数字变全角 | `3.3.2` → `3．3．2` | 全角 ASCII 转半角 |
| 偶尔漏字 | `重新匹配最快网络` → `重 新 匹 最 快 网 络` | 只做包含匹配，不要求相等 |
| 长英文串会糊 | 网络名基本不可读 | 不指望它，仍靠树/截图 |

## 三条硬约束怎么落地的

1. **坐标只算一次**：`viewPointToWindow(snapshot, view, {u,v})` 是唯一入口，
   返回 `space:'window'`；**不加窗口原点、不乘 DPI**（原点由 `toolset.click`
   的 `space:'window'` 分支加）。越界抛 `OutOfRange`，**绝不 clamp**。
2. **证据与结论分开**：OCR 分数只进 `scores.textRecognition`；
   候选的 `role` / `actionability` 恒为 `unknown`；`kind` 只写来源真正知道的
   （`text` / `point`）。
3. **ID 不透明 + 级联失效**：`candidateId` 随机生成；作废一张截图会同时带走
   它的 view 和 candidate，`invalidateOwner` 处理换会话/撤权。

## 踩到的两个坑（都写进注释了）

1. **WinRT 的 `StorageFile.GetFileFromPathAsync` 不吃正斜杠路径**。
   `C:/Users/...` 会抛 `E_INVALIDARG`，包在 `IAsyncOperation` 里表现成
   `Wait 时发生一个或多个错误` —— 完全看不出是路径问题。`ocr-worker.mjs` 里统一转反斜杠。
2. **测试要截图时不能用 `%TEMP%`**：helper 跑在 Low 完整性下，写工作区之外被沙箱拒
   （`OutputNotWritable`）。测试改成落在 `.cache/` 下。
   ——"出题人比被测进程权限高"时，不能按自己的方便挑目录。

## 最低验收集现在到哪了

评审列了 8 条，这一批覆盖第 1、2 条，并且是**真机真截图**：

| # | 内容 | 状态 |
|---|---|---|
| 1 | 同一源图、不同裁图尺寸 → 同一窗口像素；含裁剪偏移、两轴独立舍入 | ✅ |
| 2 | 空 OCR / 中文 / 重复同名 / 错误字段 / 越界 → 不误选、不夹边 | ✅ |
| 3 | 窗口没动但内容变了 → 旧候选拒绝 | ⬜ 下一批（要 ROI 复查） |
| 4 | OCR 期间截图被淘汰/撤权/会话结束 → 迟到结果不能登记 | ⬜（存储层已就绪，缺 generation 检查） |
| 5 | 推理结束到点击之间按 ESC → 输入仍被闸门拦下 | ⬜ |
| 6 | 已发送但超时 → 只报不确定，不二次点击 | ⬜ |
| 7 | 缺依赖/管道断/协议不符 → 有界失败 | 🟡 脚本缺失/无语言包返回结构化错误，未测进程被杀 |
| 8 | 打包部署位置跑一次完整流程；100%/125% 落点 | ⬜（新增文件已进 `sync-plugin.sh` 与 `package.json`） |

## 全量测试

32 个测试文件全绿（新增 `test-visual-candidates.mjs`，29 项断言）。


---

# 第二批：接进工具（2.18.39）

## 新增的东西

| 位置 | 内容 |
|---|---|
| `helper` | `region_hash`（现在屏幕某块像素的指纹）、`image_hash`（一张已存 PNG 里某块区域的指纹）。同一套 FNV-1a，两边必须给出同一个值 |
| `plugin/index.mjs` | `recordObservation`：截图登记**收成一个入口**（原来是 `desktop_screenshot` 与 `observeAfter` 各写一遍）；登记项新增 `ownerId` / `generation` / `pngSha256` / 解码出的 `image` 尺寸 |
| 工具 | `desktop_visual_candidates`（读旧图 → OCR → 候选列表）、`desktop_click_candidate`（按候选点击，**发输入前**做像素复查） |
| 测试 | `test-region-hash.mjs`（8 项）、`test-visual-tools.mjs`（10 项） |

## 点击流程（和评审要求对齐）

```
取候选（不占执行权，只读旧图）
  → click_candidate 取执行权
  → 校验：shot 存在 / 归属本会话 / 未过期 / 文件摘要与登记一致 / 候选属于这张 shot
  → 换算坐标（viewPointToWindow，唯一一条）
  → **复查**：image_hash(旧图区域) vs region_hash(现在屏幕同一块)
        不一致 → 拒绝，不发送任何输入
  → set.tools.click（desktop 空间 + expectedGeom：窗口一移就拒）
  → observeAfter
```

## 实测发现的两件事

1. **复查余量 12px 会把邻居吃进来**。第一版用 12px 余量，候选「剩余流量」旁边就是
   实时流量计数，于是每次复查都判"内容变了" —— 误拒率 100%。改成 4px
   （能看出位移与覆盖，又不至于把邻居框进来），并把"误拒要靠真实任务统计"写进注释。
2. **真实应用的窗口在我们跑测试期间被移动过好几次**（空闲时不移动，也不像测试干的：
   助手侧的 `click` 只做 SetCursorPos + down/up，激活层不动坐标）。
   后果是"截图里的那块区域"和屏幕上的不是同一块，复查这一格失去前提。
   测试现在会检测窗口位移并**跳过**，而不是给出一个假的结论。

## 第 3 条怎么变成确定性的

原来靠 SuYou 做对照：截图 → 外部点一下切页 → 用旧候选点。三个不确定源全在环境里
（窗口会被移动、页面状态、实时数字），所以它经常只能跳过。

现在改成 `tools/fixtures/recheck-window.ps1` 造一个**自己会变脸**的窗口：

```
0.0s  窗口出现，显示 ALPHA-ONE
      截图 → 取候选（ALPHA-ONE）
2.5s  窗口自己把文字改成 BETA-TWO（窗口没动、几何完全一致）
3.2s  用旧候选点 → 拒绝「那块区域的内容已经变了」，不发送任何输入
      重新截图 → 取候选（BETA-TWO）→ 点得动（正向对照）
```

三个断言，零依赖环境状态。

顺带一个坑：这个 fixture 必须 **TopMost**。截图是 `CopyFromScreen` 抓窗口矩形那块**屏幕**，
窗口被别的窗口盖住时抓到的是别人的像素 —— 第一次跑就把 DSH 的日志文字 OCR 出来了，
而失败信息看起来像"候选里没有 ALPHA-ONE"。

## 现在能演示什么

```
desktop_screenshot → shot-66988-…
desktop_visual_candidates({hwnd, shotId, query:"剩余流量"})
  → 13 条候选，带不透明 ID 与图片内坐标
desktop_click_candidate({hwnd, shotId, candidateId})
  → 已点击候选「剩 余 流 量」…复查：那块 XXxYY 像素与截图时一致（指纹 …）
     通过现有链路：白名单 / 几何 / 落点归属都查过了
```

## 还没做的（评审 8 条里仍空着的）

| # | 内容 | 状态 |
|---|---|---|
| 3 | 内容变了 → 旧候选拒绝 | ✅ `test-visual-recheck.mjs`：自造窗口**自己改文字**，确定性验证拒绝 + 正向对照 |
| 4 | OCR 期间截图被淘汰 / 撤权 / 会话结束 → 迟到结果不能登记 | ⬜ 需要 generation 在 OCR 前后各查一次 |
| 5 | 推理结束到点击之间按 ESC | ⬜ 闸门在 helper 侧已生效，但没有专门的端到端测试 |
| 6 | 已发送但超时 → 只报不确定、不二次点击 | ⬜ |
| 7 | worker 缺依赖 / 管道断 / 协议不符 | 🟡 缺脚本、无语言包有结构化报错；进程被杀未测 |
| 8 | 打包部署位置跑完整流程；100%/125% | ⬜ 文件已在打包清单里 |

## 全量测试

33 个测试文件通过；`test-visual-tools` 10 项通过、1–2 项按环境跳过。
