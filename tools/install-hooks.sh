#!/bin/sh
# 安装提交钩子。\`.git/hooks/\` 不进版本库，所以把它写成一个可重放的脚本 ——
# 换机器、重新 clone 之后跑一次就恢复。
#
# 用法：sh tools/install-hooks.sh
set -eu

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

HOOK_DIR=$(git rev-parse --git-path hooks)
mkdir -p "$HOOK_DIR"

cat > "$HOOK_DIR/pre-commit" <<'EOF'
#!/bin/sh
# 由 tools/install-hooks.sh 生成。真正的逻辑在 tools/precommit.sh。
exec "$(git rev-parse --show-toplevel)/tools/precommit.sh"
EOF
chmod +x "$HOOK_DIR/pre-commit"
chmod +x tools/precommit.sh 2>/dev/null || true

echo "已安装：$HOOK_DIR/pre-commit"
echo "跑快测：sh tools/precommit.sh"
echo "跳过一次：SKIP_TESTS=1 git commit ..."
