#!/bin/sh
# 提交前跑"快测"：只跑不需要真实桌面环境的那批。
#
# 为什么不跑全部：全量要 3~4 分钟，而且一半文件要真窗口、会被遮挡/最小化干扰，
# 当提交门禁会变成"每次都失败然后大家加 --no-verify"。快测 29 秒、0 跳过、纯逻辑，
# 更适合当门禁；桌面相关的靠手动 `node tools/run-all.mjs`。
#
# 逃生口：
#   SKIP_TESTS=1 git commit -m "..."   跳过（日志里会说"未验证"）
#
# 退出码：测试失败 → 1（阻止提交）；找不到 node → 0 但**大声说明没有验证**。

set -u
ROOT=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
cd "$ROOT" || exit 0

if [ -n "${SKIP_TESTS:-}" ]; then
  echo "pre-commit: SKIP_TESTS 已设置 —— 本次提交**没有跑测试**"
  exit 0
fi

NODE_BIN="${NODE_BIN:-}"
if [ -z "$NODE_BIN" ]; then
  if command -v node >/dev/null 2>&1; then
    NODE_BIN=node
  elif [ -x "/c/Users/a1838/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/node/bin/node.exe" ]; then
    NODE_BIN="/c/Users/a1838/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/node/bin/node.exe"
  fi
fi

if [ -z "$NODE_BIN" ]; then
  echo "pre-commit: 找不到 node —— 本次提交**没有跑测试**（不是通过）"
  echo "            设 NODE_BIN=/path/to/node 后重试，或用 SKIP_TESTS=1 明确跳过"
  exit 0
fi

echo "pre-commit: 跑快测（约 30 秒；桌面相关测试不在其中）"
"$NODE_BIN" tools/run-all.mjs --fast
status=$?
if [ $status -ne 0 ]; then
  echo
  echo "pre-commit: 测试未通过，提交被拦下。"
  echo "            修完再提交；确实要跳过用 SKIP_TESTS=1（会在提交历史里留下痕迹的只有你自己的记忆）"
fi
exit $status
