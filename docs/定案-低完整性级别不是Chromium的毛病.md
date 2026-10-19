# 定案：dsc.exe 看不见 Chromium，是**完整性级别**，不是父进程链

> 一句话：同一份 `dsc.exe`，放在 DSH 的工作区里跑是 **Low** 完整性 → 读 SuYou **7** 个节点；
> 同样字节复制到 `%TEMP%` 就是 **Medium** → 读到 **20~40** 个。
> 差别不在"谁启动它"，在"它自己以什么级别在跑"。

## 怎么查出来的

起点是第十轮那句"同一窗口在 helper 能不能读之间来回翻转"。分两步走：

**第一步**：`maxNodes` 伪影（已在 2.18.33 修掉）——helper 的 `named/actionable` 只数列出的行，
`maxNodes=8` 时前 8 行全是 `Pane` → 判"树不可用" → 换桥。

**第二步**：剩下的"DSH 里读 40、我的 shell 里读 7"。先按"谁启动"查，结果全都不成立：

| 启动链 | 结果 |
|---|---|
| `bash → dsc.exe`（仓库副本） | 7 |
| `bash → powershell → dsc.exe`（仓库副本） | 7 |
| `bash → python → dsc.exe`（仓库副本） | 7 |
| `explorer → DSH → dsc.exe`（插件的**部署副本**） | 40 |
| `explorer → DSH → 探针 → dsc.exe`（仓库副本） | 7 |

链条长度、中间有没有 .NET、是不是 DSH 拉起来的，全都不影响。

**关键一击**：把两个副本**交替跑**（同一个 shell、同一秒、字节完全相同）：

```
A(helper/bin, 仓库内)=total:7     ← 3 次都一样
C(node_modules, 部署)=total:20    ← 3 次都一样
```

同字节不同结果，那变量只能是"文件在哪"。于是把同一份 exe 复制到几个地方：

| 位置 | `total` |
|---|---|
| `D:\dsh workspace\...\helper\bin\` | 7 |
| `D:\dsh workspace\dsc-test2.exe` | 7 |
| `D:\dsh workspace\...\.cache\` | 7 |
| `D:\has space\dsc.exe` | **20** |
| `D:\nospace\dsc.exe` | **20** |
| `%TEMP%\dsc.exe` | **20** |

边界正好是**工作区目录**。`icacls` 一看就明白了：

```
icacls "D:\dsh workspace"
  Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)
```

工作区被 DSH 的沙箱打了 **Low 完整性标签**（可继承），而从那里执行的程序**也以 Low 运行**。
用探针量出来：

```
D:\dsh workspace\...\.probe\who.exe   →  integrity=S-1-16-4096 (Low)
C:\Users\...\Temp\who.exe            →  integrity=S-1-16-8192 (Medium)
```

## 结论

1. **Low 完整性的 UIA 客户端拿不到这些应用的内容树**：`FindAll(Children)` 只返回外壳
   （Window + TitleBar + 5 个 Pane = 7），于是 `BoundedWalk` 无从下探。
   Medium 的同一个二进制能读到 20~40 个，含 `Document`/`Button`/`Text` 与矩形。
2. 插件本身没问题：它用的是**安装目录**里的那份（`~/.dsh/profiles/...`，Medium），
   所以一直读得到。我这边所有测试跑在仓库里（Low），所以一直读不到 ——
   我一直以为那是 Chromium 的毛病。
3. 桥依然有价值，但理由变了：它由 python 直接起 PowerShell（**都在工作区外，Medium**），
   所以它读得到；不是因为"要绕开 .NET"。

## 第八轮那个根因要撤回

第八轮把"Chromium 读不到"归因于**调用链里有 .NET 进程**
（`.NET → powershell` 读到 5，`bash → powershell` 读到 39）。现在看，那张表更可能是
**完整性级别**的伪装：

- 那些"有 .NET 祖先"的链，全是 **由 `dsc.exe` 拉起来的**；而 `dsc.exe` 就在工作区里 → Low
  → 它拉起的 `python`/`powershell` 全都继承 Low → 当然读不到。
- 反过来，"没有 .NET 祖先"的链是 `bash → python → powershell`，**没经过 dsc.exe** → Medium → 39。

直接反证也拿到了：**Medium 的 dsc.exe + .NET 父进程（powershell）→ 20 个节点**。
如果"链里有 .NET 就瞎"成立，这一格应该只有 5 个。

要彻底钉死，还得把第八轮那张表用"helper 放到工作区外"重跑一遍 —— 这次没做，标注为
"高置信，未重跑"。

## 改了什么

1. **helper 自报级别**：`ping` 与 `tree` 都带 `integrity`（Low/Medium/…）与 `integrityRid`（4096/8192）。
   踩到的坑：第一版用 `OpenProcess(自己 pid)` 取令牌，**在 Low 下恒失败**，于是"级别低"
   被报成"未知"—— 正好把唯一的线索掐掉。改用自身伪句柄 + SID 字符串解析后正常
   （`GetSidSubAuthority*` 那条在这台机器上恒失败，换了能验证成功的那条）。
2. **读残时先排除自己**：`desktop_read_window` 在"树不可用 + 自己 Low"时直接说清楚 ——
   dsc.exe 跑在低完整性下，拿不到内容树，这不是应用的问题；并指出该用安装目录里那份。
3. 回归测试 `tools/test-integrity-level.mjs`：`ping` 必须报出级别（挡住"未知"回归）；
   再把同一份 exe 复制到临时目录对比 —— 本机是 `Low/7` vs `Medium/40`，
   没有 Low 标签的机器上这一格会自动跳过。

## 对使用者意味着什么

- 正常安装（插件用 profile 里那份）**不受影响**，Chromium 内容树读得到。
- 只有"从工作区里直接跑 `helper/bin/dsc.exe`"（开发、调试、测试）才会遇到 7 个节点。
  这时候读到的树不代表应用的真实可访问性。
- 如果哪天树又变残了，先看返回里的 `integrity`：**Low 就先换位置，别查应用。**
