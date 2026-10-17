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

# 观察类工具的授权策略。
#
# 这一条防的是"洞被改回来"。读界面内容和截图原先不需要授权，
# 于是助手可以在不问用户的情况下读到观影记录、聊天窗口 —— 这不会报错，
# 只会静默泄露，所以必须用断言守住。
head_ "7. 观察类工具的授权策略"
# 检查**部署的那份**，不是源码——实际跑起来的才算数。
#
# 用独立脚本而不是内联 heredoc：早先内联的版本把参数传丢、
# 又把错误吞了，于是故意破坏代码它照样报 OK，等于没有检查。
DEST="${DEST:-$HOME/.dsh/profiles/desktop/node_modules/dsh-desktop-control}"
if [ -n "$NODE" ] && [ -f "$DEST/toolset.mjs" ]; then
  if "$NODE" tools/check-authpolicy.mjs "$DEST/toolset.mjs"; then
    ok "授权策略符合（读树/截图需授权）"
  else
    bad "授权策略不符 —— 见上面明细"
  fi
else
  echo "  （跳过：找不到 node 或部署的 toolset.mjs）"
fi

head_ "8. 部署目录自洽（相对 import 都有对应文件）"
# 这一条来自一次真事故：新增 tooldsl.mjs 后 sync 的硬编码文件列表没带上它，
# 于是部署目录缺文件 → 模块加载失败 → 插件激活失败 → 所有工具消失。
# 源码目录和 tgz 里文件都齐，所以只有"部署目录"这一处能暴露它。
if [ -n "$NODE" ] && [ -d "$DEST" ]; then
  if "$NODE" tools/check-deploy.mjs "$DEST"; then
    ok "部署目录自洽"
  else
    bad "部署目录不自洽 —— 见上面明细"
  fi
else
  echo "  （跳过：找不到 node 或部署目录）"
fi

head_ "9. helper 命令的守卫（急停 + 白名单）"
# 这两道检查是逐命令手写的，漏掉不会有任何报错——命令只是照常工作。
# 实测踩到两次：tree/screenshot 漏白名单（空白名单下照样读完整界面文字）、
# tree/screenshot/launch 漏急停（闸门拉下后照样读、照样启动程序）。
# 第二条尤其要紧：用户按 ESC 的意图是"别再碰我的电脑"。
if [ -n "$NODE" ] && [ -f helper/src/Program.cs ]; then
  if "$NODE" tools/check-helper-guards.mjs helper/src/Program.cs; then
    ok "所有命令的守卫齐全"
  else
    bad "有命令缺守卫 —— 见上面明细"
  fi
else
  echo "  （跳过：找不到 node 或 helper 源码）"
fi

head_ "10. 应用名解析（不许把事做到别人身上）"
# 模糊匹配自动启动 → "微信"被解析成"企业微信"并启动；
# 清单指向卸载程序 → 照它启动会去卸载软件。两条都是静默的危险行为。
if [ -n "$NODE" ]; then
  if "$NODE" tools/test-apps.mjs >/dev/null 2>&1; then
    ok "精确匹配可用 / 模糊匹配不自动启动 / 无卸载程序条目"
  else
    bad "应用解析有问题 —— 单独跑 node tools/test-apps.mjs 看明细"
  fi
else
  echo "  （跳过：找不到 node）"
fi

head_ "11. 坐标空间与点击验证（缺陷 4 回归）"
# 图内坐标当桌面坐标传 → 点到别处还报成功。现在 space="window" 自己加原点，
# 且返回里回显最终落到的桌面坐标，传错能立刻看出来。
if [ -n "$NODE" ] && [ -f helper/bin/dsc.exe ]; then
  if "$NODE" tools/test-click-space.mjs >/dev/null 2>&1; then
    ok "space 换算正确 / 拒绝时给出换算结果 / 超范围不白问授权"
  else
    bad "坐标空间有问题 —— 单独跑 node tools/test-click-space.mjs 看明细"
  fi
else
  echo "  （跳过：找不到 node 或 dsc.exe）"
fi

head_ "12. 截图必须作为图像进入模型（审查报告第 5 条）"
# 只返回路径时，模型要看界面得再调一次 read_image —— 每轮多一次往返，
# 这正是"比 Codex 慢"的结构原因。图像必须走 attachments.saveImages()。
if [ -n "$NODE" ]; then
  if "$NODE" tools/test-image-block.mjs >/dev/null 2>&1; then
    ok "schema 有 image / render 带图像 / 真实 PNG 进 attachment / 缺服务时降级并警告"
  else
    bad "截图没有进入模型 —— 单独跑 node tools/test-image-block.mjs 看明细"
  fi
else
  echo "  （跳过：找不到 node）"
fi

head_ "13. 审查报告的修复项（目标绑定 / 描述符 / 树剪枝 / 重复枚举）"
if [ -n "$NODE" ] && [ -f helper/bin/dsc.exe ]; then
  if "$NODE" tools/test-review-fixes.mjs >/dev/null 2>&1; then
    ok "click 绑定 --hwnd / set_value 认描述符 / ComboBox 不剪 / 不重复列窗口"
  else
    bad "审查修复项有问题 —— 单独跑 node tools/test-review-fixes.mjs 看明细"
  fi
else
  echo "  （跳过：找不到 node 或 dsc.exe）"
fi

head_ "14. 测试自身的过滤器（别把光带当成目标窗口）"
# dsc-esc 的光带是全屏窗口，尺寸上完全符合"够大的窗口"。选中它必然被拒，
# 而报出来的错会指向完全无关的地方。这个过滤器第一版写错了分隔符，
# 看着在、实际没滤掉——所以单独验一次。
if [ -n "$NODE" ]; then
  if "$NODE" tools/test-isourwindow.mjs >/dev/null 2>&1; then
    ok "isOurs 同时认反斜杠与正斜杠，字段缺失不崩"
  else
    bad "过滤器判据不对 —— 单独跑 node tools/test-isourwindow.mjs"
  fi
else
  echo "  （跳过：找不到 node）"
fi

head_ "15. 截图→记录→点击引用 这条链（2.9.2 的断点）"
# toolset 只返回 filepath/text 时，插件记下来的几何全是 undefined，
# 于是每次带 shotId 的点击都误报"窗口尺寸从 undefinedxundefined 变了"。
if [ -n "$NODE" ]; then
  if "$NODE" tools/test-shot-chain.mjs >/dev/null 2>&1; then
    ok "几何作为字段返回 / shotId 模型可见 / 伪造 ID 被拒 / 无误报"
  else
    bad "截图坐标链断了 —— 单独跑 node tools/test-shot-chain.mjs"
  fi
else
  echo "  （跳过：找不到 node）"
fi

head_ "16. 受控测试靶（可核对落点与输入）"
# 会弹出一个测试窗口，几秒后自己关掉。这条不放进常规自检是为了不打断你，
# 但它是唯一能证明"点到的真是那个按钮"的测试 —— 重要改动手动跑：
#   node tools/test-target.mjs
if [ "${DSC_SELFCHECK_TARGET:-0}" = "1" ] && [ -n "$NODE" ] && [ -f tools/target/bin/dsc-target.exe ]; then
  if "$NODE" tools/test-target.mjs >/dev/null 2>&1; then
    ok "落点命中预期按钮 / set_value 内容一致 / 同名按钮被拒"
  else
    bad "靶子测试失败 —— 单独跑 node tools/test-target.mjs"
  fi
else
  echo "  （跳过：会弹出测试窗口，需要时用 DSC_SELFCHECK_TARGET=1 开启）"
fi

head_ "17. 急停闸门不能被收尾动作解除（复核报告的两条漏洞）"
# desktop_off 曾经 halt 之后 --clear，把用户按 ESC 的急停删掉；
# gate_probe 曾经拿真实闸门做写删测试，调一次诊断就解除一次急停。
if [ -n "$NODE" ] && [ -f helper/bin/dsc.exe ]; then
  if "$NODE" tools/test-gate-safety.mjs >/dev/null 2>&1; then
    ok "闸门在收尾/诊断前后原样保留（存在、内容都不变）"
  else
    bad "闸门会被静默解除 —— 单独跑 node tools/test-gate-safety.mjs"
  fi
else
  echo "  （跳过：找不到 node 或 dsc.exe）"
fi

head_ "18. 用户按 ESC 之后必须安静地停住（实测反馈的 bug）"
# 用户原话："我物理按 esc 中止后，其实我的意思就是任务先暂停，但你老是会执拗的打开"。
# 根因是 ensureIndicator 不看闸门：模型每重试一次，指示层就被重新拉起一次。
if [ -n "$NODE" ]; then
  if "$NODE" tools/test-halt-quiet.mjs >/dev/null 2>&1; then
    ok "急停生效期间不拉起指示层，且告诉用户怎么解除"
  else
    bad "急停之后指示层还会冒出来 —— 单独跑 node tools/test-halt-quiet.mjs"
  fi
else
  echo "  （跳过：找不到 node）"
fi

head_ "19. 改变布局的动作必须让旧截图坐标失效"
# 滚轮滚动后，同一张截图上的坐标全部指向别处。不作废的话旧 shotId 会
# 通过全部检查（窗口没移、没改大小、没过期），然后点到完全不同位置。
if [ -n "$NODE" ]; then
  if "$NODE" tools/test-shot-invalidate.mjs >/dev/null 2>&1; then
    ok "wheel/drag/type 后作废旧 shotId，新截图不受影响"
  else
    bad "旧截图坐标未失效 —— 单独跑 node tools/test-shot-invalidate.mjs"
  fi
else
  echo "  （跳过：找不到 node）"
fi

head_ "20. helper 命令行行为（第三方反馈的三个 bug 的回归）"
# 这三个不是崩溃，而是【静默的错误语义】，只能靠行为断言守住：
#   stop 是查询却有写副作用、press_key 把窗口句柄当按键回显、
#   --timeout 承诺了自动消失但根本没实现。
if [ -n "$NODE" ] && [ -f helper/bin/dsc.exe ]; then
  if "$NODE" tools/test-helper-cli.mjs helper/bin >/dev/null 2>&1; then
    ok "stop 只读 / halt 有效 / press_key 回显正确 / --timeout 不写闸门"
  else
    bad "命令行行为不符 —— 单独跑 node tools/test-helper-cli.mjs 看明细"
  fi
else
  echo "  （跳过：找不到 node 或 dsc.exe）"
fi

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
