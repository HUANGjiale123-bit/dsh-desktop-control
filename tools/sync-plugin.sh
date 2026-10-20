#!/usr/bin/env bash
#
# 把插件同步到 profile 的安装目录。
#
# 为什么需要这个脚本：这个仓库里 toolset.mjs 有**两份**——
#   server/toolset.mjs  ← 源头（MCP 服务器与插件共用）
#   plugin/toolset.mjs  ← 插件包里的副本
# 它们必须逐字节一致，否则会出现"改了源码但跑的是旧的"这种极难发现的问题。
#
# 本项目已经踩过一次：会话级授权写进 server/toolset.mjs 之后，
# 复制的是**过期的** plugin/toolset.mjs，于是重新部署、重启、测试，
# 得到的是旧的错误消息（WhitelistEmpty），看起来像"新功能没生效"，
# 实际是"新代码根本没被复制过去"。
#
# 所以：分发前一律跑这个脚本，不要手工 cp 单个文件。
#
# 用法：
#   ./tools/sync-plugin.sh              # 同步到默认 profile
#   ./tools/sync-plugin.sh <目标目录>    # 同步到指定目录

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO/plugin"
DEST="${1:-$HOME/.dsh/profiles/desktop/node_modules/dsh-desktop-control}"

echo "源:   $SRC"
echo "目标: $DEST"
echo

# ① 单一源头：server/toolset.mjs → plugin/toolset.mjs
#    这一步不能省。plugin/ 下的那份是副本，不是源。
if [ ! -f "$REPO/server/toolset.mjs" ]; then
  echo "错误：找不到 $REPO/server/toolset.mjs" >&2
  exit 1
fi
cp "$REPO/server/toolset.mjs" "$SRC/toolset.mjs"
# apps.mjs 也住在 server/：toolset.mjs import 它，所以测试要能直接在
# server/ 下 import（放 plugin/ 的话，测 server/toolset.mjs 就会找不到）。
cp "$REPO/server/apps.mjs" "$SRC/apps.mjs"
# UIA 读取桥：Chromium 系在 dsc.exe（.NET）里读不到内容，
# 要由这个非 .NET 的 python 脚本代读。少拷它 = 桥静默失效，
# 而失效的表现只是"读到的节点少"，不会报错 —— 正是这个脚本底部
# 那段注释说的那类事故。
cp "$REPO/server/uia-bridge.py" "$SRC/uia-bridge.py"
# 视觉兜底（候选注册 + OCR 后端）：这三个是新增模块，漏拷的后果和桥一样——
# 功能静默消失（readWindow 少了视觉路径，而工具照常返回"读不到"）。
cp "$REPO/server/visual-candidates.mjs" "$SRC/visual-candidates.mjs"
cp "$REPO/server/ocr-worker.mjs" "$SRC/ocr-worker.mjs"
cp "$REPO/server/win-ocr.ps1" "$SRC/win-ocr.ps1"
echo "① server/toolset.mjs → plugin/toolset.mjs"

# ② 辅助程序：helper/bin → plugin/bin
for exe in dsc.exe dsc-esc.exe dsc-fx.exe dsc-guard.exe; do
  if [ -f "$REPO/helper/bin/$exe" ]; then
    mkdir -p "$SRC/bin"
    cp "$REPO/helper/bin/$exe" "$SRC/bin/$exe"
    echo "② helper/bin/$exe → plugin/bin/$exe"
  else
    echo "  警告：helper/bin/$exe 不存在，先跑 ./helper/build.sh" >&2
  fi
done

# ③ 同步到目标
mkdir -p "$DEST/bin"

# 内容相同就不拷；拷不动（文件被运行中的进程占用）只警告，不中断整次同步。
#
# 踩到：ESC 监听器 dsc-esc.exe 一直在跑，而它就住在目标目录里。
# 每次同步到 cp 那一步就 "Device or resource busy" 直接退出（set -e），
# 于是 dsc.exe / toolset.mjs 都没同步过去，而脚本只留下一行 cp 错误 ——
# 看起来像"同步成功"（因为其他步骤的 echo 已经打过了）。
copy_if_changed() {
  local src="$1" dst="$2"
  local a b
  a=$(md5sum "$src" 2>/dev/null | cut -d' ' -f1)
  b=$(md5sum "$dst" 2>/dev/null | cut -d' ' -f1)
  if [ -n "$a" ] && [ "$a" = "$b" ]; then
    return 0
  fi
  if ! cp "$src" "$dst" 2>/dev/null; then
    echo "  ⚠ 覆盖失败（可能正被运行中的进程占用）：$dst" >&2
    echo "    内容与源不同，重启那个进程后再同步一次。" >&2
    return 1
  fi
  return 0
}

# uia-bridge.py 必须一起拷：Chromium 系的读取桥靠它。
# 漏了它的表现不是报错，而是"读到的节点变少"—— 静默降级，最难查的那种。
sync_failed=0
for f in index.mjs toolset.mjs tooldsl.mjs apps.mjs cordis.patch.yml package.json uia-bridge.py visual-candidates.mjs ocr-worker.mjs win-ocr.ps1; do
  copy_if_changed "$SRC/$f" "$DEST/$f" || sync_failed=1
done
for exe in dsc.exe dsc-esc.exe dsc-fx.exe dsc-guard.exe; do
  if [ -f "$SRC/bin/$exe" ]; then
    copy_if_changed "$SRC/bin/$exe" "$DEST/bin/$exe" || sync_failed=1
  fi
done
# 光标装饰素材（缺了只会"静默不启动"，所以必须一起同步）
mkdir -p "$DEST/assets/cursor"
mkdir -p "$DEST/assets/cursor/poses"
if [ -f "$SRC/assets/cursor/fish-arrow.png" ]; then
  copy_if_changed "$SRC/assets/cursor/fish-arrow.png" "$DEST/assets/cursor/fish-arrow.png" || sync_failed=1
fi
for sub in poses poses-arrow; do
  mkdir -p "$DEST/assets/cursor/$sub"
  for f in "$SRC/assets/cursor/$sub"/*; do
    [ -f "$f" ] || continue
    copy_if_changed "$f" "$DEST/assets/cursor/$sub/$(basename "$f")" || sync_failed=1
  done
done
echo "③ 已同步到 $DEST"
echo

# ④ 校验：源与目标逐文件比对
echo "校验（源 ↔ 目标）："
fail=0
for f in index.mjs toolset.mjs tooldsl.mjs apps.mjs cordis.patch.yml package.json uia-bridge.py visual-candidates.mjs ocr-worker.mjs win-ocr.ps1 bin/dsc.exe bin/dsc-esc.exe bin/dsc-fx.exe; do
  a=$(md5sum "$SRC/$f" 2>/dev/null | cut -d' ' -f1 || echo missing)
  b=$(md5sum "$DEST/$f" 2>/dev/null | cut -d' ' -f1 || echo missing)
  if [ "$a" = "$b" ] && [ "$a" != "missing" ]; then
    printf "  ✓ %-22s %s\n" "$f" "${a:0:8}"
  else
    printf "  ✗ %-22s 源=%s 目标=%s\n" "$f" "${a:0:8}" "${b:0:8}"
    fail=1
  fi
done

# ⑤ 额外校验：确认新代码确实在部署的那份里
if grep -q "effectiveAllowlist" "$DEST/toolset.mjs" 2>/dev/null; then
  echo "  ✓ 部署的 toolset.mjs 含会话授权代码"
else
  echo "  ✗ 部署的 toolset.mjs **不含**会话授权代码（复制了过期文件？）"
  fail=1
fi

# 闸门路径必须由插件统一传给 dsc.exe。
# 不传的话 dsc.exe 会自己推一个不同的路径，ESC 写了 A 而它查 B——
# 界面消失了但操作照常执行，属于"看起来生效了其实没停"。
if grep -q "DSC_HALT_GATE" "$DEST/toolset.mjs" 2>/dev/null; then
  echo "  ✓ 部署的 toolset.mjs 会把 DSC_HALT_GATE 传给 dsc.exe"
else
  echo "  ✗ 部署的 toolset.mjs **不传** DSC_HALT_GATE —— ESC 会停不掉操作"
  fail=1
fi

# 检查"删了定义但留下引用"这类错误。
#
# 两次踩到同一个坑：删掉一个常量（gatePath、edgePath），却漏掉某处引用。
# 未定义标识符是**合法语法**，`node --check` 查不出来，只在运行时抛异常；
# 而插件 apply 抛异常的表现是"所有工具消失"——连诊断工具也没了，
# 于是失去了排查手段。所以在这里静态挡一道。
echo
echo "检查残留的未定义引用："
LEFTOVER=0
for name in edgePath gatePath bundledEdge repoEdge overlayPath; do
  # 排除合法写法：config.X / _X / X: / 'X' 之类的定义或属性访问
  hits=$(grep -n "${name}" "$DEST/index.mjs" 2>/dev/null          | grep -vE "config\.${name}|_${name}|${name}:|'${name}'|\* |// " || true)
  if [ -n "$hits" ]; then
    echo "  ✗ ${name} 可能有未定义引用："
    echo "$hits" | head -3 | sed 's/^/      /'
    LEFTOVER=1
  fi
done
[ "$LEFTOVER" -eq 0 ] && echo "  ✓ 未发现残留引用"

echo
if [ "$fail" -eq 0 ] && [ "$LEFTOVER" -eq 0 ] && [ "$sync_failed" -eq 0 ]; then
  echo "同步完成。重启 DSH 让插件重新加载。"
else
  if [ "$sync_failed" -ne 0 ]; then
    echo "有文件没能同步过去（多半是被运行中的进程占用，见上面 ⚠）。" >&2
  fi
  echo "同步有问题，见上面 ✗ 标记。" >&2
  exit 1
fi
