# 上架 DSH 插件市场（awesome-dsh-plugin）— 操作单

> 结论先说：**市场不是注册制，是 PR 制。** 列表来自仓库
> [`awesome-dsh-plugin/awesome-dsh-plugin`](https://github.com/awesome-dsh-plugin/awesome-dsh-plugin)
> 的 `data/plugins/*.yml`，合并后站点与市场自动收录（通常一天内）。
> 我们**现在还不能提交**：硬门槛是**仓库创建满 1 天**，本仓库建于
> `2026-10-21T09:05:20Z`（北京时间 17:05），所以最早 **2026-10-22 17:05 之后**。

## 条目文件（已备好，待提交）

路径与文件名（`owner__repo--子目录.yml`）：

```
data/plugins/HUANGjiale123-bit__dsh-desktop-control--plugin.yml
```

内容：

```yaml
url: https://github.com/HUANGjiale123-bit/dsh-desktop-control/tree/main/plugin
name: HUANGjiale123-bit/dsh-desktop-control#plugin
category: tools
tarball: https://github.com/HUANGjiale123-bit/dsh-desktop-control/releases/latest/download/dsh-desktop-control.tgz
description:
  en: 'Windows desktop observation and control: accessibility tree, screenshots, OCR fallback, mouse and keyboard, with session-scoped approvals and an ESC halt gate.'
  zh: Windows 桌面观察与操作：无障碍树、截图、OCR 兜底、鼠标键盘，带会话级授权与 ESC 急停。
```

字段说明：

- `url` 指向**子目录** `plugin/`，因为 `dsh.bundle` 声明在 `plugin/package.json`，
  仓库根没有 `package.json`——合集仓库的子包用这种写法
- `tarball` 指向**版本无关**的 Release 资产 `dsh-desktop-control.tgz`
  （已上传到 v1.0.2；用 `latest/download/` 时文件名不能带版本号，否则下次发版 404）
- `description.en` 里有 `: `，**必须加引号**，否则 YAML 解析失败
- `category: tools`（可选值里没有 desktop/automation）

## 提交前检查清单

- [x] `plugin/package.json` 声明 `dsh.bundle.patch` → `cordis.patch.yml`
- [x] 仓库有真实代码（25 个工具 + 4 个原生 exe 源码）
- [x] 仓库 topic 含 `dsh-plugin`
- [x] Release 有版本无关 tarball：`dsh-desktop-control.tgz`
- [ ] **仓库满 1 天**（2026-10-22 17:05 后）
- [ ] 提 PR：fork → 加上述一个文件 → 开 PR（只改这一个文件）

## 提交后

- CI 会检查：manifest、仓库年龄、YAML 格式、README 是否可重新生成
- 维护者会读一遍仓库再合并；被打回会指出具体改哪一行，改好再提即可
- 合并后 `main` 上自动重新生成两个 README，站点与市场收录通常一天内生效

## 备注

- 市场显示的是**条目**，不是 `dsh.bundle` 里的行；装的时候仍走
  `dsh plugin add` 或市场的安装按钮
- 收录 ≠ 安全审查；描述里不要写营销词（审稿会打回）
