/* 有界遍历 —— **纯逻辑，不碰 COM**。
 *
 * ── 为什么单独抽出来 ──
 *
 * 原来的遍历器直接写在 Probe.cs 里，和 IAccessible 缠在一起，结果
 * **没法用它自己的数据测它**。外部复核对这个函数做了纯内存复现，
 * 抓到三个确定的错误：
 *
 *   1 父 + 2 个简单子元素   → 应该 3 个节点，实际 3000（撞预算）
 *   1 父 + 70 个完整对象孩子 → 应该 71 个，实际 65（**没有截断标记**）
 *   一个对象把自己列为孩子   → 应该 1 个，实际 41
 *
 * 根因是：**简单子元素的 Owner 是父对象**，而旧代码无条件枚举
 * cur.Owner 的孩子 —— 等于把父对象的孩子当成这个孩子的后代，无限递归。
 *
 * 这解释了实测里的 "36 个重复输入法" 和 "71 个同矩形"。
 *
 * 所以这个文件只做遍历逻辑，节点从哪来由调用方给（生产用 IAccessible，
 * 测试用内存里的假数据）。**逻辑必须能被自己的数据检验。**
 */

using System;
using System.Collections.Generic;

namespace Dsc
{
    /// <summary>一个节点对遍历器可见的静态信息。</summary>
    internal sealed class WalkNode
    {
        public string Identity;      // 本次遍历内唯一。简单子元素 = 父身份 + "#" + childId
        public string Name;
        public string Role;
        public int[] Rect;
        public bool HasDefaultAction;
    }

    /// <summary>一次枚举的结果。</summary>
    internal sealed class WalkPage
    {
        public List<object> Children = new List<object>();
        public int Obtained;         // provider 实际给了几个
        public int Total = -1;       // provider 声称总共几个（未知时 -1）
        public string FailReason;    // 非空 = 这次枚举失败
    }

    /// <summary>遍历结果。**保留每一个未完成原因**，不在汇总时清空。</summary>
    internal sealed class WalkResult
    {
        public List<WalkNode> Nodes = new List<WalkNode>();
        public List<int> Depths = new List<int>();
        /**
         * 唯一节点数。
         *
         * 原来是普通字段，在函数末尾赋值 —— 于是 `node_budget` 提前 return
         * 的分支里它永远是 0。测试第 4 项就是这么抓到它的：
         * 走满了 10 个节点，却报 "节点数 = 0"。
         *
         * 改成属性就不会再漏：任何出口拿到的都是实际值。
         */
        public int UniqueCount { get { return Nodes.Count; } }
        public int DuplicateEdges;      // 遇到已经见过的节点：记数，不再展开
        public int RectCount;
        public string TruncatedBy;      // node_budget / depth_budget / enum_failed / null
        /** **只表示当次可达范围走完了**，不是"覆盖了应用内容"。 */
        public bool TraversalComplete;
        public bool HitCycle;
        public int MaxDepthSeen;
    }

    /// <summary>栈里的一项：节点 + 它的深度。深度由遍历器维护，不由调用方猜。</summary>
    internal sealed class WalkEntry
    {
        public object Node;
        public int Depth;
    }

    internal static class BoundedWalk
    {
        /// <summary>
        /// 遍历一个节点树。
        ///
        /// 调用方提供四件事：怎么读、怎么枚举、怎么识别身份、这个节点是不是
        /// 简单子元素。后者是关键 —— **简单子元素不能再展开**，它的属性由
        /// 父对象提供，枚举它的 Owner 会把父对象的兄弟节点当成它的后代。
        /// </summary>
        internal static WalkResult Run(
            object root,
            Func<object, WalkNode> read,
            Func<object, int, int, WalkPage> children,   // (node, start, count)
            Func<object, bool> isSimpleChild,
            int maxNodes,
            int maxDepth)
        {
            var result = new WalkResult();
            if (root == null) { result.TraversalComplete = true; return result; }
            if (maxNodes <= 0) maxNodes = 3000;
            if (maxDepth <= 0) maxDepth = 40;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<WalkEntry>();
            stack.Push(new WalkEntry { Node = root, Depth = 0 });

            while (stack.Count > 0)
            {
                if (result.Nodes.Count >= maxNodes)
                {
                    result.TruncatedBy = "node_budget";
                    return result;
                }

                WalkEntry entry = stack.Pop();
                object cur = entry.Node;
                int depth = entry.Depth;

                WalkNode info = read(cur);
                if (info == null) continue;

                // ── 身份去重 ──
                //
                // 同一节点可能通过多条路径到达（环、重复子元素引用）。
                // 见过就只记数、不再展开 —— 否则会无限膨胀。
                // 注意：**不按 Name + Rect 去重**，不同按钮可能同名，
                // 也可能暂时拥有相同矩形。
                if (seen.Contains(info.Identity))
                {
                    result.DuplicateEdges++;
                    result.HitCycle = true;
                    continue;
                }
                seen.Add(info.Identity);

                result.Nodes.Add(info);
                result.Depths.Add(depth);
                if (info.Rect != null) result.RectCount++;
                if (depth > result.MaxDepthSeen) result.MaxDepthSeen = depth;

                if (depth >= maxDepth)
                {
                    if (result.TruncatedBy == null) result.TruncatedBy = "depth_budget";
                    continue;
                }

                // ── 简单子元素到此为止 ──
                //
                // 这是修掉那个 3000 的关键：它的 Owner 是父对象，
                // 再枚举就是父对象的孩子，会无限循环。
                if (isSimpleChild(cur)) continue;

                // ── 分页枚举 ──
                //
                // 旧代码永远问 start=0 的前 64 个，第 65 个以后直接消失，
                // 而且**不报截断**。现在按 obtained 推进 offset。
                int offset = 0;
                const int PageSize = 64;
                while (true)
                {
                    WalkPage page = children(cur, offset, PageSize);
                    if (page == null || !string.IsNullOrEmpty(page.FailReason))
                    {
                        if (result.TruncatedBy == null)
                            result.TruncatedBy = page == null ? "enum_failed" : page.FailReason;
                        break;
                    }
                    if (page.Obtained <= 0) break;     // 这一页空了 = 该对象枚举完

                    int take = page.Children.Count;
                    for (int i = take - 1; i >= 0; i--)
                        stack.Push(new WalkEntry { Node = page.Children[i], Depth = depth + 1 });

                    offset += page.Obtained;

                    if (page.Total >= 0 && offset >= page.Total) break;
                    if (page.Obtained < PageSize) break;   // provider 给不满 = 到底了
                }
            }

            result.TraversalComplete = result.TruncatedBy == null;
            return result;
        }
    }
}
