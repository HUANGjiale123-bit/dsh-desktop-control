#!/usr/bin/env bash
#
# 一条命令回答："现在这套东西还能不能跑？"
#
# 为什么需要它：代码规模涨到几千行之后，"上次提交是好的"不足以作为依据
# ——提交与提交之间可能已经坏了，而症状往往要等真去操控桌面时才暴露。
# 这个脚本把能在本地验的东西一次跑完，并给出明确的通过/失败。
#
# 它**不需要 DSH 在跑**，也不碰桌面（不移动鼠标、不启动应用），
# 所以随时可以执行，不会干扰正在做的事。
#
# 用法：./tools/selfcheck.sh

set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

PASS=0
FAIL=0
ok()   { echo "  ✓ $1"; PASS=$((PASS+1)); }
bad()  { echo "  ✗ $1"; FAIL=$((FAIL+1)); }
head_() { echo; echo "── $1 ──"; }

NODE=""
for c in \
  "/c/Users/a1838/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/node/bin/node.exe" \
  "$(command -v node 2>/dev/null || true)"
do
  [ -n "$c" ] && [ -x "$c" ] && { NODE="$c"; break; }
done

head_ "1. 编译"
BUILD_OUT="$(./helper/build.sh 2>&1)"
if echo "$BUILD_OUT" | grep -q "error CS"; then
  bad "编译报错："
  echo "$BUILD_OUT" | grep "error CS" | head -5 | sed 's/^/      /'
else
  ok "dsc.exe / dsc-esc.exe 编译通过"
fi

head_ "2. 两个二进制都在且能启动"
for exe in dsc.exe dsc-esc.exe; do
  if [ -f "helper/bin/$exe" ]; then
    ok "helper/bin/$exe 存在 ($(stat -c%s "helper/bin/$exe") bytes)"
  else
    bad "helper/bin/$exe 缺失"
  fi
done
if ./helper/bin/dsc.exe ping 2>/dev/null | grep -q '"ok":true'; then
  ok "dsc.exe ping 正常"
else
  bad "dsc.exe ping 失败"
fi

head_ "3. 已删除的东西没有被加回来"
[ -f helper/src/Edge.cs ] && bad "Edge.cs 又出现了（光带已合进 dsc-esc）" || ok "Edge.cs 不存在"
[ -f helper/bin/dsc-edge.exe ] && bad "dsc-edge.exe 又出现了" || ok "dsc-edge.exe 不存在"
[ -f plugin/bin/dsc-edge.exe ] && bad "plugin/bin/dsc-edge.exe 又出现了" || ok "plugin/bin 里没有 dsc-edge.exe"

head_ "4. 自测脚本"
if [ -n "$NODE" ]; then
  for t in tools/test-approval.mjs tools/test-packaging.mjs; do
    R="$("$NODE" "$t" 2>&1 | tail -1)"
    if echo "$R" | grep -q "0 失败"; then
      ok "$(basename "$t"): $R"
    else
      bad "$(basename "$t"): $R"
    fi
  done
else
  bad "找不到 node，跳过自测"
fi

head_ "5. 源码与部署一致"
SYNC_OUT="$(./tools/sync-plugin.sh 2>&1)"
if echo "$SYNC_OUT" | grep -q "✗"; then
  bad "同步校验有失败项："
  echo "$SYNC_OUT" | grep "✗" | head -5 | sed 's/^/      /'
else
  ok "8 项哈希全部一致（含 DSC_HALT_GATE 传递检查）"
fi

head_ "6. 闸门生命周期"
G="D:\\dsh workspace\\dsh-desktop-control\\.state\\halt"
DSC_HALT_GATE="$G" ./helper/bin/dsc.exe stop --clear >/dev/null 2>&1
DSC_HALT_GATE="$G" ./helper/bin/dsc.exe halt "selfcheck" >/dev/null 2>&1
H1="$(DSC_HALT_GATE="$G" ./helper/bin/dsc.exe ping 2>/dev/null | grep -o '"halted":[a-z]*')"
DSC_HALT_GATE="$G" ./helper/bin/dsc.exe stop --clear >/dev/null 2>&1
H2="$(DSC_HALT_GATE="$G" ./helper/bin/dsc.exe ping 2>/dev/null | grep -o '"halted":[a-z]*')"
[ "$H1" = '"halted":true' ]  && ok "拉下闸门后 halted=true"  || bad "拉下闸门无效（$H1）"
[ "$H2" = '"halted":false' ] && ok "解除闸门后 halted=false" || bad "解除闸门无效（$H2）"

echo
echo "══════════════════════════════"
if [ "$FAIL" -eq 0 ]; then
  echo "  全部通过（$PASS 项）"
  echo "  可以放心重启 DSH 验证桌面部分。"
else
  echo "  $PASS 项通过，$FAIL 项失败  ← 先修再继续"
fi
echo "══════════════════════════════"
exit $([ "$FAIL" -eq 0 ] && echo 0 || echo 1)
