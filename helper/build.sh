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

CSC='C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
FW='C:/Windows/Microsoft.NET/Framework64/v4.0.30319'

if [ ! -f "/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" ]; then
    echo "找不到 csc.exe —— 这台机器没有 .NET Framework 4.x？" >&2
    exit 1
fi

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
echo "自检:"
./bin/dsc.exe ping
