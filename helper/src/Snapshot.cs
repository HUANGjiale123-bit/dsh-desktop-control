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

        /// <summary>抓取一份快照：树文本 + 索引表 + 动作表，来自同一次遍历。</summary>
        public static Snapshot Capture(AutomationElement root, IntPtr hwnd, int maxNodes)
        {
            Snapshot snap = new Snapshot();
            snap.Hwnd = hwnd;
            snap.Revision = NextRevision();

            AutomationElementCollection all =
                root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
            snap.Total = all.Count;
            int limit = Math.Min(all.Count, maxNodes);
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
                    if (!double.IsInfinity(b.Left) && !double.IsNaN(b.Left) && b.Width > 0)
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

            if (snap.Total > limit)
                sb.Append("… 另有 ").Append(snap.Total - limit).Append(" 个元素未列出（受 maxNodes 限制）\n");

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
