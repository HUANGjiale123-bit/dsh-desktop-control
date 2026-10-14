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
echo "① server/toolset.mjs → plugin/toolset.mjs"

# ② 辅助程序：helper/bin → plugin/bin
for exe in dsc.exe dsc-edge.exe dsc-esc.exe; do
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
for f in index.mjs toolset.mjs cordis.patch.yml package.json; do
  cp "$SRC/$f" "$DEST/$f"
done
for exe in dsc.exe dsc-edge.exe dsc-esc.exe; do
  [ -f "$SRC/bin/$exe" ] && cp "$SRC/bin/$exe" "$DEST/bin/$exe"
done
echo "③ 已同步到 $DEST"
echo

# ④ 校验：源与目标逐文件比对
echo "校验（源 ↔ 目标）："
fail=0
for f in index.mjs toolset.mjs cordis.patch.yml package.json bin/dsc.exe bin/dsc-edge.exe bin/dsc-esc.exe; do
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

echo
if [ "$fail" -eq 0 ]; then
  echo "同步完成。重启 DSH 让插件重新加载。"
else
  echo "同步有问题，见上面 ✗ 标记。" >&2
  exit 1
fi
