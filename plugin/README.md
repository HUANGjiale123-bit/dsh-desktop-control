# dsh-desktop-control

让 DSH 能看见你的屏幕、操作你的鼠标键盘 —— 带一套"随时能停"的机制。

**先说清楚它是什么、以及它有什么毛病，再决定要不要装。** 下面「已知问题」那一节
不是免责声明，是实测结果，请先读。

---

## 安装

**不需要任何外部依赖**（2.6.0 起）——插件只用相对路径与 Node 内置模块，
所以装到哪都能加载。

需要 DSH 版本支持 `ctx.approval`（会话授权）。装好后**重启 DSH**。

### ⚠️ 装完必须做两步，少一步插件不会激活（而且没有任何报错）

**DSH 只加载 profile `package.json` 里 `dsh.profile.bundles` 数组中的包。**
`pnpm add` 只把它装进 `node_modules` 并写 `dependencies`，**不会登记 bundle**。

如果只做第 1 步，结果是：**所有 `desktop_*` 工具都报 `unknown tool`，没有任何其它提示** ——
看起来就像插件是坏的。这是第三方实测里 100% 会踩的一步。

**第 1 步：装包**

```bash
# 把 dsh-desktop-control-*.tgz 放到某处，然后：
cd <你的 DSH profile 目录>          # 例如 ~/.dsh/profiles/desktop
pnpm add file:<tarball 的绝对路径>
```

（也可以从解压后的目录装：`pnpm add file:<目录绝对路径>`）

**第 2 步：登记 bundle —— 这一步不能省**

```bash
# 先看现在有没有：
cd <你的 DSH profile 目录>
node -e "const p=require('./package.json');console.log(p.dsh?.profile?.bundles||[])"
```

输出里**必须**有 `"dsh-desktop-control"`。没有就手动加进 `package.json` 的
`dsh.profile.bundles` 数组（和 `@deepseek-ai/dsh-base` 那些并列）：

```json
{
  "dsh": {
    "profile": {
      "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dsh-desktop-control"]
    }
  }
}
```

改完**重启 DSH**。

**装完的自检**（一条命令看清两个位置的状态）：

```bash
cd <你的 DSH profile 目录>
node -e "const p=require('./package.json');const inDeps=!!p.dependencies?.['dsh-desktop-control'];const inBundles=(p.dsh?.profile?.bundles||[]).includes('dsh-desktop-control');console.log('dependencies:',inDeps?'✓':'✗');console.log('bundles:',inBundles?'✓':'✗');if(inDeps&&!inBundles)console.log('\n★ 包装上了但没登记 bundle —— 插件不会激活。按上面第 2 步补。');if(!inDeps)console.log('\n★ 还没装包，先做第 1 步。');"
```

**为什么插件自己不能提示这件事**：插件没被登记就根本不会被加载，
它的任何代码都不会执行 —— 所以这个提醒只能放在文档里，或者由外部命令给出。

### 指示层多久自动收起

自动拉起的指示层（光带 + 逃生条）在**最后一次操作后 8 秒**自动收起，不写闸门。

要改这个时长，在 profile 的 `cordis.patch.yml` 里加配置：

```yaml
- id: desktop-control
  package: dsh-desktop-control
  config:
    idleHideMs: 30000      # 毫秒；0 表示永不自动收起
```

- 设大一点（例如 30000）：连续操作时不会闪，代价是停手后它还会挂一会儿。
- 设 0：永不自动收 —— 但 `dsc-esc` 自己还有个 5 分钟兜底上限。
  真要长时间常驻请用 `desktop_indicate`，那条路径完全不设超时。

**为什么默认这么短**：实测助手一轮推理约 40 秒，所以任何低于 40 秒的值
都会在两轮操作之间消失再出现。既然闪无可避免，就选"停手后很快就没了"
这半边体验 —— 挂着的"正在被控制"才是更烦的那个。

要"做完立刻收"，最准的信号是助手在收尾时主动调 `desktop_off`；
超时只是保底。

### 卸载

`pnpm remove dsh-desktop-control` **只会清依赖，不会清 `bundles`**，
留下一条指向不存在包的悬空引用（DSH 启动时会为此报错）。所以卸载要两步：

```bash
# ① 先从 bundles 里删掉 "dsh-desktop-control"
# ② 再卸包
pnpm remove dsh-desktop-control
```

**可选：自定义补丁条目**

插件自带 `cordis.patch.yml`，登记 bundle 后会自动生效，通常不需要手写条目。
如果要覆盖默认配置，才需要在 profile 的 `cordis.patch.yml` 里加：

```yaml
- id: desktop-control
  package: dsh-desktop-control
```

**注意 `inject` 必须写在 YAML 条目里**，只导出模块级 `inject` 不生效——
插件自带的 `cordis.patch.yml` 会处理好，但如果手写条目，四个服务都要列：

```yaml
inject: ["tools", "approval", "agents", "workspaceRegistry", "attachments"]
# attachments 是把截图像素交给模型用的 —— 少了它模型只能拿到路径。
```

重启后 `desktop_diagnose` 可用就说明装上了。它会把版本、程序位置、
闸门路径的解析过程全打出来——**出问题先看它**。

---

## 它能做什么

| 工具 | 说明 |
|---|---|
| `desktop_list_apps` | 列出已安装应用（**不需授权**） |
| `desktop_launch_app` | 启动应用（**先授权再启动**） |
| `desktop_list_windows` | 列窗口（不需授权，**不含标题**） |
| `desktop_read_window` | 读界面无障碍树（**需授权**） |
| `desktop_screenshot` | 截图（**需授权**） |
| `desktop_click` | 坐标点击（需授权） |
| `desktop_click_element` | 按元素描述符点击（需授权，**比坐标可靠**） |
| `desktop_type` / `desktop_set_value` | 输入文本（需授权，`set_value` 带回读校验） |
| `desktop_press_key` | 按键，如 `Ctrl+S`、`Enter`（需授权） |
| `desktop_do_action` | 次要无障碍动作（需授权） |
| `desktop_indicate` | 显示状态光带 + 逃生条 |
| `desktop_off` | 停止并清除状态 |
| `desktop_revoke_grants` | 收回本次会话的全部授权 |
| `desktop_diagnose` | 诊断 |

**指示层会自动出现**：任何变更性操作放行后，如果光带和逃生条没在跑，
插件会自动拉起它们。**不需要谁记得去开。**

---

## 安全模型

**默认什么都不放行。** 应用白名单初始为空。

```
第一次操作某个应用  →  弹询问  →  你同意  →  本次会话内该应用不再问
                                        →  会话结束自动失效，不写任何配置
```

**观察也算访问。** 读界面内容和截图同样要授权——树里有界面上全部文字，
截图就是界面本身。

`list_windows` 刻意不返回窗口标题：标题里经常就有敏感内容（文档名、
聊天对象、正在看的剧集），而"有哪些窗口"这件事不需要读内容。

---

## 随时能停

**按物理 ESC 键。** 三条停止路径：

```
物理 ESC   →  全局键盘钩子捕获（不经过鼠标，恶意程序抢不走）
点逃生条   →  屏幕上方的文字（逃生条本身捕获鼠标点击）
关掉窗口   →  也是停止
```

停止后写一个"闸门文件"，之后**所有变更性操作被拒**并返回 `已被急停`。

**注意**：闸门拉下后不会自动解除。要继续用得先清掉：

```
dsc.exe stop --clear
```

（ESCAPE 键的机制解释见文末"为什么 ESC 可靠"。）

---

## 已知问题 —— 请先读这一节

### ① 现代应用的无障碍树往往没用

实测五个真实应用，全部落在"树信息极少"一侧：

| 应用 | 节点数 | 可交互元素 |
|---|---|---|
| DeepSeek Harness（Electron） | 4 | 0 |
| 微信（Electron） | 4 | 0 |
| Windows 设置（WinUI） | 1 | 0 |
| WinUI 记事本 | 10 | 0 |
| 腾讯视频 | 2 | 0 |

**意思是**：Electron 和 WinUI 应用经常几乎不暴露无障碍信息。
对这类应用，读树拿不到有用的东西，得靠截图看、靠 `click_element` 或
"文本输入 + 回车"来操作。

`desktop_read_window` 会**在树太稀疏时明确告诉你**，并给出替代做法。
不会让你拿到一棵只有 2 个 Pane 的树还以为读到了界面。

### ② 坐标点击在部分应用上不可靠

实测：腾讯视频的搜索按钮，用无障碍树给出的矩形算出的坐标**连点两次毫无反应**，
而改用回车立刻生效。

**所以点击优先用 `desktop_click_element`（按元素描述符定位，不经过坐标）。**
坐标点击只在没有可描述元素时用（画布、视频画面）。

### ③ 同一个窗口的树有时能读到时读不到

实测同一天里，同一个腾讯视频窗口曾读到过 60 个节点（含可写的搜索框），
之后每次只读到 2 个。

**原因未查清。** 试过这些都没能复现出那个丰富的树：把窗口激活到前台、
连续重复读、用一个独立的 UIA 客户端做真实的子树遍历查询
（Chromium 文档说这是懒惰启用无障碍的机制）。

所以 `read_window` 的提示写的是"**本次读取**信息很少"，不是"这个应用没有树"——
前者是我验证过的，后者不是。

### ④ 只在 Windows 上测过，且只在一台机器上

DPI 缩放、注册表结构、沙箱行为全是**一台机器**上的实测结果。
换台机器可能不一样。

### ⑤ 手敲 `dsc.exe` 时闸门路径的某条回退分支没实测

正常情况下插件会显式传闸门路径，这条分支不生效。它只在直接运行
`dsc.exe`（不经插件）且 exe 所在的目录不可写时才会走到。
逻辑是按"实际写一个探测文件"来判断的，但**"回退到 %LOCALAPPDATA%" 这一步
我没能在这台机器上造出只读目录来实测**（ACL 被组策略挡着）。

### ⑥ 只测过三个应用的操作

WinForms 自建窗口、WinUI 记事本、腾讯视频。其余是推断。

---

## 为什么 ESC 可靠

恶意程序可以抢鼠标（`SetCursorPos` + 合成点击），但**抢不走物理键盘**。

所以停止机制用的是全局键盘钩子（`SetWindowsHookExW(WH_KEYBOARD_LL)`），
它直接读物理按键，合成按键带 `LLKHF_INJECTED` 标志会被识别并排除。

**钩子生效有个前提**：宿主进程必须有一个可见窗口并且被激活过至少一次。
`dsc-esc.exe` 启动时会自己激活一次然后释放焦点，所以这个前提是满足的。

日志里能看到它区分两者：

```
[11881ms] 真实按键 vk=0x1B  <= ESC          ← 物理按键
[45022ms] 心跳：入口 0 次，真实按键 0 次     ← 合成的输入不计入
```

---

## 试用的建议路径

```
① desktop_diagnose        确认装上了、看闸门路径
② desktop_list_windows    列窗口（不需要授权，看看能不能看到你的应用）
③ desktop_launch_app 记事本   应该弹授权，同意后记事本启动
④ desktop_indicate        光带 + 逃生条应该出现
⑤ 按物理 ESC              应该全部消失
⑥ desktop_read_window     读记事本的树（会弹授权）—— 大概率会看到"树信息很少"的提示
```

第 ⑥ 步看到那个提示是**正常的**，不是装坏了。

---

## 遇到问题

**先跑 `desktop_diagnose`**，它会打出：

```
插件版本、是否用 gateChecked 构建
helper / esc / plugin 的实际路径
闸门路径的完整解析过程（config → workspaceRegistry → 最终结果）
静态白名单与会话授权的当前内容
```

插件激活失败时会注册一个**应急版** `desktop_diagnose`，只返回异常与堆栈——
这种情况下也能查到原因，不会只剩一句 "unknown tool"。
