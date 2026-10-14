# profile 备份清单

备份时间: 2026-10-14 19:14:03
源目录:   C:\Users\a1838\.dsh\profiles\desktop

## 备份原因

准备删除 cordis.patch.yml 里手写的 desktop-control 条目，
让插件自带的 bundle（plugin/cordis.patch.yml）生效——那是别人从市场安装时的路径。
如果结果不是预期的，用本目录的文件还原。

## 还原方法

  cp backup/profile-desktop-*/cordis.patch.yml "C:\Users\a1838\.dsh\profiles\desktop\cordis.patch.yml"
  然后重启 DSH

## 备份时的关键状态

```
profile/cordis.patch.yml 里的 desktop-control 条目:
  - id: desktop-control
    name: dsh-desktop-control
    config:
      helperPath: "D:\\dsh workspace\\dsh-desktop-control\\helper\\bin\\dsc.exe"
      edgePath: "D:\\dsh workspace\\dsh-desktop-control\\helper\\bin\\dsc-edge.exe"
      shotDir: "D:\\dsh workspace\\dsh-desktop-control\\.cache\\shots"
      allowedApps:
        - notepad.exe
        - QQLive.exe
        - DoubleTarget.exe
  - id: terminal-controller
    name: "@deepseek-ai/dsh-api-terminal-controller"
    config:

已安装插件版本:
    "version": "0.3.0",

插件目录内容:
  bin
  cordis.patch.yml
  index.mjs
  package.json
  toolset.mjs

插件 bin/ 内容:
  dsc-edge.exe
  dsc.exe
```
