#!/usr/bin/env bash
# 编译 Windows 辅助进程。
#
# Git Bash 与老 csc 的组合有两个坑，构建脚本都绕开了：
#
#   坑一：MSYS 会转换看起来像路径的参数，`-nologo` 被误伤成 `D:/Git/nologo`，
#         csc 报 "未能找到源文件"。应对：csc 与程序集引用一律写成 `C:/...`，
#         MSYS 见到盘符就不再转换，选项保持单斜杠也不受影响。
#
#   坑二：老 csc（v4.8.9221，C# 5）把路径里的 `/` 当作选项分隔符，
#         `src/Program.cs` 会被截成 `Program.cs`，报 "未能找到源文件"，
#         且报文里显示的是被截断后的路径。应对：源文件路径用反斜杠 `src\Program.cs`。
#
# DPI 感知在程序内声明，见 src/Program.cs 的 SetProcessDPIAware。

set -euo pipefail

cd "$(dirname "$0")"

# 探测 csc.exe：不同机器上 .NET Framework 的落点不一样，32/64 位也可能只有一边。
# 顺带试 Roslyn（若装了 SDK 或 VS），它有更好的语言支持。
find_csc() {
    local cands=(
        "/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        "/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe"
        "/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe"
        "/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe"
    )
    for c in "${cands[@]}"; do
        [ -f "$c" ] && { echo "$c"; return 0; }
    done
    return 1
}

find_fw() {
    for d in \
        "/c/Windows/Microsoft.NET/Framework64/v4.0.30319" \
        "/c/Windows/Microsoft.NET/Framework/v4.0.30319"
    do
        [ -f "$d/WPF/UIAutomationClient.dll" ] && { echo "$d"; return 0; }
    done
    return 1
}

CSC_MSYS="$(find_csc)" || {
    echo "找不到 C# 编译器。" >&2
    echo "需要 .NET Framework 4.x（Windows 自带），或 Visual Studio 的 Roslyn。" >&2
    exit 1
}
FW_MSYS="$(find_fw)" || {
    echo "找不到 WPF 的 UIAutomation 程序集（$CSC_MSYS 旁边应有 WPF 目录）。" >&2
    exit 1
}

# 转成 Windows 风格路径给 csc：它不认 MSYS 的 /c/... 形式，
# 而反斜杠形式还能绕开"老 csc 把 / 当选项分隔符"的坑。
win() { printf '%s' "$1" | sed -e 's|^/c/|C:/|' ; }
CSC="$(win "$CSC_MSYS")"
FW="$(win "$FW_MSYS")"

echo "编译器: $CSC"
echo "框架  : $FW"

mkdir -p bin

echo "编译 dsc.exe ..."
"$CSC" \
    -nologo \
    -target:exe \
    -platform:x64 \
    -out:bin/dsc.exe \
    -r:System.dll \
    -r:System.Drawing.dll \
    -r:System.Windows.Forms.dll \
    -r:"$FW/WPF/UIAutomationClient.dll" \
    -r:"$FW/WPF/UIAutomationTypes.dll" \
    -r:"$FW/WPF/WindowsBase.dll" \
    'src\Program.cs'

echo
echo "完成: bin/dsc.exe ($(stat -c%s bin/dsc.exe) bytes)"

echo
echo "编译 dsc-edge.exe （整屏 computer use 状态指示）..."
"$CSC" \
    -nologo \
    -target:winexe \
    -platform:x64 \
    -out:bin/dsc-edge.exe \
    -r:System.dll \
    -r:System.Drawing.dll \
    -r:System.Windows.Forms.dll \
    'src\Edge.cs'

echo "完成: bin/dsc-edge.exe ($(stat -c%s bin/dsc-edge.exe) bytes)"

echo

echo
echo "自检:"
./bin/dsc.exe ping
