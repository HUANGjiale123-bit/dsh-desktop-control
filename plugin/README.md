# dsh-desktop-control

让 DSH 能看见你的屏幕、操作你的鼠标键盘——带一套"随时能停"的机制。

**先说清楚它是什么、以及它有什么毛病，再决定要不要装。** 下面「已知问题」那一节
不是免责声明，是实测结果，请先读。

当前版本：**2.18.65**（完整设计与实测记录在仓库 `docs/` 下，`docs/README.md` 是入口）

---

## 它提供什么

25 个工具，分四组：

| 组 | 工具 |
|---|---|
| 观察 | `desktop_list_windows`（不弹授权）、`desktop_read_window`、`desktop_screenshot`、`desktop_whatis` |
| 操作 | `desktop_click`、`desktop_click_element`、`desktop_type`、`desktop_press_key`、`desktop_set_value`、`desktop_move`、`desktop_drag`、`desktop_hold`/`release`、`desktop_wheel`、`desktop_do_action` |
| 视觉兜底 | `desktop_visual_candidates`（本地 OCR 找文字）、`desktop_visual_view`（裁图放大）、`desktop_click_candidate` |
| 生命周期 | `desktop_launch_app`、`desktop_list_apps`、`desktop_indicate`、`desktop_off`、`desktop_resume`、`desktop_diagnose`、`desktop_revoke_grants` |

**授权**：碰窗口内容的操作（读树、截图、点击、输入、拖拽…）会先弹会话级授权，
只对当前会话的该应用有效；列表/诊断这类不碰内容的不会问。

---

## 安装

1. 在 DSH 的插件管理里从本地文件安装本 tgz
2. **重启 DSH**

**不推荐手改配置文件**。DSH 只加载 profile `package.json` 里 `dsh.profile.bundles`
数组中的包；手动 `pnpm add` 只装进 `node_modules` 不会激活（表现为所有 `desktop_*`
工具报 `unknown tool`）。更糟的是，那个数组是手写 JSON——**写坏一个逗号，整个 DSH
起不来**。

如果你必须手动装，**顺序是三步，少一步都不行**：

1. 装进 profile：`cd <profile>; pnpm add file:<tgz 绝对路径>`
2. 备份 `package.json`（`copy package.json package.json.bak`），往 `dsh.profile.bundles`
   里加 `"dsh-desktop-control"`（注意最后一项后面不能有逗号），然后立刻校验：
   `node -e "JSON.parse(require('fs').readFileSync('package.json','utf8')); console.log('OK')"`；
   没打印 `OK` 就 `copy package.json.bak package.json`
3. 重启 DSH，跑 `desktop_diagnose`：看到 `插件 2.18.xx` 才算成功

**不要**手改 `cordis.yml` / `cordis.patch.yml`——本包自带 patch，手写 YAML 条目
容易写错字段（早期说明就错在让人写 `package:` 那一项）。

**弄坏了怎么救**：关掉 DSH → 还原 `package.json.bak`（或删掉多加的那一行并检查逗号）
→ 校验 JSON → 重启。实在不行，把 profile 目录**改名**（不要删）成 `<名字>.broken`，
DSH 会重建一个干净的 profile；确认没问题后再决定要不要清掉旧的。

需要 DSH 的 Node ≥ 18；Windows 10/11。

---

## 配置

全部可选，写在插件的 `config` 里：

| 键 | 作用 |
|---|---|
| `allowedApps` | 持久放行的应用（如 `["notepad.exe"]`）。留空=每次问 |
| `shotDir` | 截图输出目录（默认系统临时目录下） |
| `helperPath` / `escPath` | 覆盖辅助程序路径（默认随包自带） |
| `gatePath` | 急停闸门文件位置 |
| `ocrLanguage` | OCR 语言（默认随系统，取不到用中文） |
| `idleHideMs` | 指示层空闲自动隐藏时间 |
| `cursorFx` | 光标装饰开关（默认 **关**） |
| `cursorFxSize` | 装饰尺寸（默认 120） |
| `cursorFxMode` | `blank`（透明光标，默认）/ `pose`（第二套无箭头素材） |

---

## 安全：急停与恢复

- **ESC**：任何时刻按下即写"闸门"文件，所有变更操作立刻被拒。闸门**不会自动解除**，
  解除方式：让模型调 `desktop_resume`、或 `bin\dsc.exe stop --clear`、或删除闸门文件
  （路径由 `desktop_diagnose` 给出）。
- **鼠标不会卡住**：按下/抬起之间进程被杀时，helper 记账 + `ProcessExit` + 超时补发释放。
- **光标装饰**：进程退出、插件卸载、被强杀（看门狗 400ms 轮询）三条路径都会恢复系统光标；
  低版本 Windows 会**自动不启用**（截图排除能力不可用时不冒这个险）。

---

## 光标装饰（默认关闭）

把系统光标换成"画里的鲸鱼 + 箭头"，并跟随系统真实光标形状（I 形/手形/加载圈/四向）。

打开：配置里写 `cursorFx: true`，或在 `%TEMP%` 建空文件 `dsc-cursor-fx.enable`，重启 DSH。

停手 30 秒后自动恢复你原来的光标。

---

## 已知问题（实测）

- **多显示器 / 跨屏不同缩放**：未验证。用 `desktop_whatis` 核对落点
- **100% 缩放**：未验证（开发机 150%）
- **游戏 / 自绘界面**：读不到内容，视觉兜底不一定可用
- **Chromium 系**：能读能点，但树里信息少
- **光标装饰**：只在一台机器上验证过；应用私有光标会退化成"显示系统光标"
- 视觉目标 < 8px 会被拒绝；动画期间可能误判

---

## 包内结构

- `bin/`：`dsc.exe`（主 helper）、`dsc-esc.exe`（ESC 监听 + 指示层）、`dsc-fx.exe`（光标装饰）、`dsc-guard.exe`（光标看门狗）
- `assets/cursor/`：光标素材（透明光标用的 10 张带箭头图 + 装饰模式用的 10 张无箭头图）
- `*.mjs`：插件本体与视觉/OCR 模块
