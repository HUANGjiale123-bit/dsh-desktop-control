# 视觉兜底 · 第一批：候选层与本地 OCR（2.18.38）

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
