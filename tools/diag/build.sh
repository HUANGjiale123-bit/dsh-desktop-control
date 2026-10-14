#!/usr/bin/env bash
# 诊断工具的独立构建。
#
# 这些程序只用于排查问题，不属于发布内容。放在这里而不是 helper/src/，
# 是因为它们一度比产品代码还多（34 个文件、约 5800 行），混在源码目录里
# 既干扰阅读，也会被主构建脚本连带编译。
#
# 用法：./build.sh <文件名不含扩展名> [...]
#   例：./build.sh CursorTruth HookDiag
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
CSC=""
for c in \
  "C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" \
  "C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe" \
  "$(command -v csc 2>/dev/null)" ; do
  [ -n "$c" ] && [ -f "$c" ] && CSC="$c" && break
done
[ -z "$CSC" ] && { echo "找不到 csc.exe"; exit 1; }

FW="$(dirname "$CSC")"
OUT="$HERE/bin"
mkdir -p "$OUT"

for name in "$@"; do
  src="$HERE/$name.cs"
  [ -f "$src" ] || { echo "跳过 $name（无此文件）"; continue; }
  # 用到 UIA / WinForms 的文件需要额外引用，统一都带上，免得逐个判断
  "$CSC" -nologo -target:exe -platform:x64 -out:"$OUT/$name.exe" \
    -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
    -r:"$FW/WPF/UIAutomationClient.dll" \
    -r:"$FW/WPF/UIAutomationTypes.dll" \
    -r:"$FW/WPF/WindowsBase.dll" \
    "$(cygpath -w "$src" 2>/dev/null || echo "$src")" 2>&1 | grep -E "error CS" | head -5
  [ -f "$OUT/$name.exe" ] && echo "  完成: bin/$name.exe" || echo "  失败: $name"
done
