#!/usr/bin/env bash
# 编译受控测试靶 dsc-target.exe。
#
# 和 helper 用同一套工具链（.NET Framework + C# 5 + 手写 csc 命令），
# 理由很实际：如果靶子需要另一套运行时才能跑，那它证明的是"另一套环境能跑"，
# 不是"这套能跑"。
#
# 产物不进发布包（plugin/package.json 的 files 里没有它）。

set -euo pipefail
cd "$(dirname "$0")"

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

CSC_MSYS="$(find_csc)" || {
    echo "找不到 C# 编译器。" >&2
    exit 1
}

win() { printf '%s' "$1" | sed -e 's|^/c/|C:/|' ; }
CSC="$(win "$CSC_MSYS")"

mkdir -p bin

echo "编译 dsc-target.exe ..."
"$CSC" \
    -nologo \
    -target:winexe \
    -platform:x64 \
    -out:bin/dsc-target.exe \
    -win32manifest:'app.manifest' \
    -r:System.dll \
    -r:System.Drawing.dll \
    -r:System.Windows.Forms.dll \
    'Target.cs'

echo "完成: bin/dsc-target.exe ($(stat -c%s bin/dsc-target.exe) bytes)"
