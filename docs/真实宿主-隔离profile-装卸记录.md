# 真实 DSH 宿主 · 隔离 profile 装卸记录

> 目的：回应复核"17/17 不能替代真实宿主重启"。
> 方式：`DSH_HOME` 指向独立目录，**不碰用户正在使用的 profile**；
> 用 DSH 自己的 CLI（`plugin --profile ... add/remove`）走宿主真实的 reconcile 路径。

环境：

```
DSH 0.2.1-rc.2
DSH_HOME = D:\dsh workspace\dsh-home-test        （独立，可随时删）
profile  = iso-l（从 headless 模板新建）
CLI      = app.asar/dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/cli.js
```

## 1. 装旧版（有 inject 缺陷的 旧构建（有缺陷））→ 启动

```
$ dsh plugin --profile iso-l add file:.../dsc.tgz
+ dsh-desktop-control 旧构建（有缺陷）

$ dsh iso-l "hi"
dsh: warning: 1 entry did not activate
desktop-control (dsh-desktop-control): pending (waiting for service: workspaceRegistry)
```

**根因**：`cordis.patch.yml` 与模块导出的 `inject` 都把 `workspaceRegistry` 列为必需；
headless profile 没有这个服务 → 插件永远 pending，**工具根本不注册**。

## 2. 升级到 修复版（inject 只留 `tools`/`approval`）→ 启动

```
$ dsh plugin --profile iso-l add file:.../dsc77.tgz
+ dsh-desktop-control 修复版

$ dsh iso-l "hi"
（没有 pending / did not activate 行 —— 插件已激活）
dsh: MISSING_CREDENTIAL: ...   # 隔离 DSH_HOME 没有 API key，预期，与插件无关
```

## 3. 卸载 → 再启动

```
$ dsh plugin --profile iso-l remove dsh-desktop-control
- dsh-desktop-control 修复版

$ grep dsh-desktop-control profiles/iso-l/package.json
（无输出：引用与依赖都已清除）

$ ls profiles/iso-l/node_modules | grep -i desktop
（无输出：包已移除）

$ dsh iso-l "hi"
（宿主正常启动，只有预期的凭据错误）
```

## 4. 结论与边界

- **装 → 升级 → 卸 → 再启动** 在真实宿主上通过；卸载后 bundles 与依赖都干净。
- 这次验证**发现了一个 mock 测不到的缺陷**（必需注入导致非 web profile 不激活），已修。
- 仍**不**证明：web/GUI profile 的完整重启体验、市场安装路径、第二台机器的环境兼容性。
- 隔离目录里没有凭据，因此每次启动停在 LLM 凭据错误——这发生在插件加载之后，
  证明宿主已经走完了插件激活阶段。
