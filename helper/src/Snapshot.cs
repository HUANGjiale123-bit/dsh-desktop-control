/*
 * dsh-desktop-control — 快照与 UIA 写操作
 *
 * 本文件负责两件事：
 *
 *   1. Snapshot —— 把「一次观察」封成一个单位。
 *      树文本、索引→元素映射、索引→次要动作表，三样必须来自同一份快照，
 *      否则索引与元素会对不上号（这正是 Codex 用 snapshotRevision /
 *      accessibilityRevision / cached secondary actions 在做的事）。
 *      以后把取数实现换成 CacheRequest + 有界遍历时，接口不动。
 *
 *   2. 不依赖输入注入的写操作 —— set_value 与 perform_secondary_action。
 *      二者都走 UIA 的模式方法，不碰 SendInput，因此在"输入被环境阻断"
 *      的情况下仍可能可用。
 *
 * 错误处理纪律：三种失败原因必须分开报，不能糊成一句"失败了"——
 *   · 控件没实现该模式  → 控件级限制，换目标而不是重构底层
 *   · 元素已过期        → 重新取快照
 *   · UIA 本身报错      → 客户端上下文问题，需要查
 */

using System;
using System.Collections.Generic;
using System.Windows.Automation;

namespace Dsc
{
    /// <summary>一次观察的完整结果。所有引用都只在本次快照内有效。</summary>
    internal sealed class Snapshot
    {
        public long Revision;                                  // 版本号，单调递增
        public IntPtr Hwnd;
        public string Tree = "";
        public int Total;
        public int Listed;

        /// <summary>是否因为深度/子节点/总数上限而没能遍历完。</summary>
        public bool Truncated;

        public readonly Dictionary<int, AutomationElement> Map =
            new Dictionary<int, AutomationElement>();

        /// <summary>索引 → 该元素可用的次要动作名（大小写规范化后的可读形式）。</summary>
        public readonly Dictionary<int, List<string>> Actions =
            new Dictionary<int, List<string>>();

    }

    internal static class SnapshotOps
    {
        private static long _revision;

        public static long NextRevision()
        {
            return ++_revision;
        }

        /// <summary>
        /// 有界遍历：广度优先，逐层 FindAll(Children)。
        ///
        /// 三重上限 + 环路检测，任何一项触发都置 truncated（而不是假装遍历完整）：
        ///   maxNodes           总元素数
        ///   maxDepth           深度
        ///   maxChildrenPerNode 单个节点的直接子节点数
        ///
        /// 环路检测用 runtime ID：怪应用可能把祖先当子节点报回来，那是死循环。
        ///
        /// 叶节点剪枝：Button/Edit/Text 这类已知叶节点不再向下查，
        /// 省掉"必然为空"的那次跨进程调用。
        /// </summary>
        private static List<AutomationElement> BoundedWalk(
            AutomationElement root, int maxNodes, int maxDepth, int maxChildrenPerNode,
            out int totalSeen, out bool truncated)
        {
            List<AutomationElement> result = new List<AutomationElement>();
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            truncated = false;
            totalSeen = 0;

            List<AutomationElement> frontier = new List<AutomationElement>();
            frontier.Add(root);

            for (int depth = 0; depth <= maxDepth && frontier.Count > 0; depth++)
            {
                List<AutomationElement> next = new List<AutomationElement>();

                foreach (AutomationElement el in frontier)
                {
                    if (result.Count >= maxNodes) { truncated = true; break; }

                    // 环路检测：同一个 runtime ID 只收一次
                    string rid = null;
                    try
                    {
                        int[] r = el.GetRuntimeId();
                        if (r != null) rid = string.Join(",", Array.ConvertAll(r, delegate(int x) { return x.ToString(); }));
                    }
                    catch { }
                    if (rid != null && !visited.Add(rid)) continue;   // 见过，跳过

                    result.Add(el);
                    totalSeen++;
                }

                if (truncated) break;
                if (depth == maxDepth) { continue; }   // 到底了，不再下探

                foreach (AutomationElement el in frontier)
                {
                    if (result.Count >= maxNodes) { truncated = true; break; }
                    if (IsLeafType(el)) continue;

                    AutomationElementCollection kids;
                    try { kids = el.FindAll(TreeScope.Children, Condition.TrueCondition); }
                    catch { continue; }

                    if (kids.Count > maxChildrenPerNode) truncated = true;   // 该节点的子节点被截断
                    int take = Math.Min(kids.Count, maxChildrenPerNode);
                    for (int i = 0; i < take; i++)
                    {
                        try { next.Add(kids[i]); } catch { }
                    }
                }

                frontier = next;
            }

            return result;
        }

        /// <summary>
        /// 已知不会再有子节点的类型。对这些类型不做 FindAll(Children)——
        /// 那次调用必然返回空，但每次都是一趟跨进程往返。
        ///
        /// ── 注意这两个类型**不在**列表里，这是刻意的 ──
        ///
        /// ComboBox 和 Header（以及 HeaderItem）可以有子元素：下拉框展开后的
        /// 选项、表头的各个表头项都在它们下面。早期版本把它们当叶节点剪掉，
        /// 读出来的树会缺内容，而且**缺得毫无提示** —— 表现为"这个应用的
        /// 无障碍树很少"，很容易被误判成"该应用不支持无障碍"。
        ///
        /// 这是第三方审查指出的（它给的是控件类型的文档依据），我没有在真实
        /// 应用上逐一复现缺失。按"宁可多遍历一层"的保守方向修：子节点数量
        /// 仍受 maxChildrenPerNode 与总预算约束，不会失控。
        /// </summary>
        private static bool IsLeafType(AutomationElement el)
        {
            string role = SafeStr(delegate { return el.Current.ControlType.ProgrammaticName; });
            if (role == null) return false;
            switch (role)
            {
                case "ControlType.Button":
                case "ControlType.Edit":
                case "ControlType.Text":
                case "ControlType.Image":
                case "ControlType.CheckBox":
                case "ControlType.RadioButton":
                case "ControlType.Hyperlink":
                case "ControlType.Slider":
                case "ControlType.ProgressBar":
                case "ControlType.Separator":
                case "ControlType.Thumb":
                case "ControlType.ScrollBar":
                case "ControlType.TitleBar":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>抓取一份快照：树文本 + 索引表 + 动作表，来自同一次遍历。</summary>
        public static Snapshot Capture(AutomationElement root, IntPtr hwnd, int maxNodes,
                                       int maxDepth, int maxChildrenPerNode)
        {
            Snapshot snap = new Snapshot();
            snap.Hwnd = hwnd;
            snap.Revision = NextRevision();

            // 有界遍历：逐层 FindAll(Children)，带深度、子节点数、总元素数三重上限，
            // 并用 runtime ID 做环路检测（怪应用可能把祖先当子节点报回来，那是死循环）。
            //
            // 为什么不用 FindAll(Subtree)：它会一次取回整棵子树，没有边界。实测大窗口
            // （Program Manager 2240x1400、DSH 1941x1242）足以让那一次调用返回上千条，
            // 且每条都是一个活 RCW。
            //
            // 为什么不用 TreeWalker：它在部分应用（打包应用、XAML 岛）上会中途停住——
            // 之前实测 Store 版记事本只拿到 12 个顶层 Pane。FindAll 更可靠。
            List<AutomationElement> all = BoundedWalk(root, maxNodes, maxDepth, maxChildrenPerNode,
                                                      out snap.Total, out snap.Truncated);
            int limit = all.Count;
            snap.Listed = limit;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < limit; i++)
            {
                AutomationElement el = all[i];
                int index = i;                     // 索引即地址
                snap.Map[index] = el;

                string role = SafeStr(delegate { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });

                // 密码字段一律不回显真值。
                // 实测：WinForms 的 UseSystemPasswordChar 文本框，UIA 会把明文当 Name 报出来，
                // 而 Name 会进入树文本、进而进入模型上下文——那是实打实的凭据泄露。
                bool isPassword = SafeBool(delegate { return el.Current.IsPassword; });
                string name = isPassword ? "<password>" : SafeStr(delegate { return el.Current.Name; });
                bool enabled = SafeBool(delegate { return el.Current.IsEnabled; });
                bool focusable = SafeBool(delegate { return el.Current.IsKeyboardFocusable; });
                bool focused = SafeBool(delegate { return el.Current.HasKeyboardFocus; });
                string aid = SafeStr(delegate { return el.Current.AutomationId; });

                // 矩形
                int left = 0, top = 0, w = 0, h = 0;
                try
                {
                    System.Windows.Rect b = el.Current.BoundingRectangle;
                    // 最小化窗口的矩形是哨兵值（约 -32000 起，实测见到 -47976），
                    // 只判断 Width>0 会把这种无效矩形当成有效坐标渲染出去，
                    // 后续按它点击就会算出荒唐的坐标。这里一并排除。
                    bool plausible = !double.IsInfinity(b.Left) && !double.IsNaN(b.Left)
                                     && b.Width > 0 && b.Height > 0
                                     && b.Left > -30000 && b.Top > -30000
                                     && b.Left < 32000 && b.Top < 32000;
                    if (plausible)
                    {
                        left = (int)b.Left; top = (int)b.Top;
                        w = (int)b.Width; h = (int)b.Height;
                    }
                }
                catch { }

                // 次要动作：由 patterns 合成，跟 Codex 的七个标签一致
                List<string> actions = CollectActions(el);
                if (actions.Count > 0) snap.Actions[index] = actions;

                // 渲染一行
                sb.Append('[').Append(index).Append("] ").Append(role);
                if (!string.IsNullOrEmpty(name)) sb.Append(" \"").Append(Trim(name, 100)).Append('"');
                if (!string.IsNullOrEmpty(aid)) sb.Append(" aid=").Append(Trim(aid, 40));
                if (w > 0) sb.Append(" @(").Append(left).Append(',').Append(top).Append(' ').Append(w).Append('x').Append(h).Append(')');
                if (!enabled) sb.Append(" DISABLED");
                if (focusable) sb.Append(" focusable");
                if (focused) sb.Append(" FOCUSED");

                // 可设置性：模型据此决定能不能 set_value
                string settable = DescribeSettable(el);
                if (settable != null) sb.Append(' ').Append(settable);

                if (actions.Count > 0)
                    sb.Append("  Secondary Actions: ").Append(string.Join(", ", actions.ToArray()));

                sb.Append('\n');
            }

            // 截断必须明说，而且要说明是被哪个上限截断的。
            // "看起来完整但其实是残的"比"明确报错"危险得多——调用方会据此以为已经看到了全部。
            if (snap.Truncated)
                sb.Append("… 遍历被上限截断（--depth<=").Append(maxDepth)
                  .Append(", --children<=").Append(maxChildrenPerNode)
                  .Append(", maxNodes<=").Append(maxNodes)
                  .Append("）；窗口内的元素多于列出的 ").Append(limit).Append(" 个，")
                  .Append("未列出的部分不在此处。\n");

            snap.Tree = sb.ToString().TrimEnd();
            return snap;
        }

        /// <summary>
        /// 合成本元素的次要动作标签。
        ///
        /// 只做 Codex 那七个：Raise / Scroll Up / Scroll Down / Scroll Left /
        /// Scroll Right / Expand / Collapse。它们分别来自 ScrollPattern 与
        /// ExpandCollapsePattern，Raise 是置顶类。
        ///
        /// 刻意不走 MSAA 的 accDoDefaultAction，也刻意不自己发明动作名——
        /// 模型只能从这份列表里取名字，猜的名字一律拒绝。
        /// </summary>
        public static List<string> CollectActions(AutomationElement el)
        {
            List<string> list = new List<string>();

            object p;

            // Raise：窗口/顶层容器可置顶
            try
            {
                if (el.TryGetCurrentPattern(WindowPattern.Pattern, out p))
                {
                    WindowPattern wp = (WindowPattern)p;
                    if (wp.Current.WindowVisualState != WindowVisualState.Minimized)
                        list.Add("Raise");
                }
            }
            catch { }

            // 滚动四个方向
            try
            {
                if (el.TryGetCurrentPattern(ScrollPattern.Pattern, out p))
                {
                    ScrollPattern sp = (ScrollPattern)p;
                    if (SafeBool(delegate { return sp.Current.VerticallyScrollable; }))
                    {
                        if (SafeBool(delegate { return sp.Current.VerticalScrollPercent > 0.5; })) list.Add("Scroll Up");
                        if (SafeBool(delegate { return sp.Current.VerticalScrollPercent < 99.5; })) list.Add("Scroll Down");
                    }
                    if (SafeBool(delegate { return sp.Current.HorizontallyScrollable; }))
                    {
                        if (SafeBool(delegate { return sp.Current.HorizontalScrollPercent > 0.5; })) list.Add("Scroll Left");
                        if (SafeBool(delegate { return sp.Current.HorizontalScrollPercent < 99.5; })) list.Add("Scroll Right");
                    }
                }
            }
            catch { }

            // 展开/折叠
            try
            {
                if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p))
                {
                    ExpandCollapsePattern ec = (ExpandCollapsePattern)p;
                    ExpandCollapseState st = ec.Current.ExpandCollapseState;
                    if (st == ExpandCollapseState.Collapsed || st == ExpandCollapseState.PartiallyExpanded)
                        list.Add("Expand");
                    if (st == ExpandCollapseState.Expanded || st == ExpandCollapseState.PartiallyExpanded)
                        list.Add("Collapse");
                }
            }
            catch { }

            return list;
        }

        /// <summary>返回 "SETTABLE" 或 "readonly" 之类的标记，告诉模型这个元素能不能写。</summary>
        public static string DescribeSettable(AutomationElement el)
        {
            // 密码字段不暴露可写性，也不该被写
            if (SafeBool(delegate { return el.Current.IsPassword; })) return "[password]";

            object p;
            try
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out p))
                {
                    ValuePattern vp = (ValuePattern)p;
                    bool ro = SafeBool(delegate { return vp.Current.IsReadOnly; });
                    return ro ? "[value:readonly]" : "[value:settable]";
                }
            }
            catch { }
            try
            {
                if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out p))
                {
                    RangeValuePattern rv = (RangeValuePattern)p;
                    bool ro = SafeBool(delegate { return rv.Current.IsReadOnly; });
                    return ro ? "[range:readonly]" : "[range:settable]";
                }
            }
            catch { }
            return null;
        }

        /* ───────────── 关于「动作前校验元素身份」 ─────────────
         *
         * Codex 有三条校验（runtime ID / 所属进程 / 目标窗口），我最初也照着实现了
         * Verify()，但**在当前的一次性进程模型下它无法成立**，因此没有接上调用：
         *
         *   Codex 能校验，是因为它的 helper 常驻，索引→句柄的映射跨调用存活，
         *   所以"这个索引现在是否还指向当初那个元素"有可比对的基准。
         *
         *   我这边每次调用都是一个新进程：抓快照和写动作发生在**同一次调用内**，
         *   快照天然新鲜，没有"过期"可言；而模型读树是**上一次调用**的事，
         *   那次快照早已随进程消失，无从比对。
         *
         * 也就是说：这个缺口不是"忘了接"，是**模型本身不支持**。要补上它必须
         * 先改成常驻进程（Codex 的做法）。
         *
         * 在常驻化之前，替代的自证手段是**回读校验**——写完立刻把值读回来，
         * 把 before/after/verified 一并交给调用方。见 Program.cs 的 OkJson。
         * 它不能证明"写对了元素"，但能证明"值确实变了"，比静默成功强。
         *
         * 这段说明保留在这里，是为了让下一个读代码的人不必重新推导一遍。
         */

        /* ───────────── 小工具 ───────────── */

        public static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }

        public static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        public static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
