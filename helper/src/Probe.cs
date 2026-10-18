/* a11y_probe —— 只读的能力探测，回答一个问题：
 *
 *   "这个窗口到底能不能拿到元素？如果能，走的是哪条通道？"
 *
 * ── 为什么先做探测而不是直接接完整遍历 ──
 *
 * 外部复核的建议很具体：不要按"Electron"这个标签一刀切地断言
 * "只能坐标"。先在同一次调用里输出：
 *
 *   目标版本、HWND 类名、UIA 初始/重查节点数、MSAA 根 HRESULT、
 *   MSAA 节点数、IA2 HRESULT、有效矩形数、截断原因
 *
 * 这样"返回空树"、"查询失败"、"初始化未完成"、"预算截断"是**四种
 * 不同的结果**，而不是一个笼统的"没元素"。
 *
 * ── 它不干什么 ──
 *
 * 不执行任何输入动作、不改目标设置、不注入 DLL。
 * 只读名字/角色/状态/默认动作描述/矩形。
 *
 * ── 输出 ──
 *
 *   probe.ok                    探测本身是否跑完（不代表拿到元素）
 *   probe.uia.nodeCount         初始 UIA 节点数
 *   probe.uia.nodeCountAfter    稍等后重查的节点数（判断是否初始化未完成）
 *   probe.msaa.hr               根 HRESULT
 *   probe.msaa.nodeCount
 *   probe.msaa.rectCount        有有效矩形的节点数
 *   probe.msaa.ia2.hr / available
 *   probe.coverage              full / partial / shell_only / none
 *   probe.notes                 人看的解释
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

namespace Dsc
{
    internal static class Probe
    {
        /// <summary>一个节点的精简描述（探测阶段只留最有用的几项）。</summary>
        private sealed class ProbeNode
        {
            public string Name;
            public string Role;
            public int[] Rect;
            public bool HasDefaultAction;
            public int Depth;
        }

        /// <summary>
        /// 有界遍历：返回节点、有效矩形数、截断原因。
        ///
        /// DFS 而不是 BFS —— 实测 BFS 拿到 400 个节点只有 3 个矩形，
        /// 因为 Chromium 的树"深而窄"，预算全花在无名容器上。
        /// </summary>
        private static void WalkBounded(
            IAccessibleShim root, List<string> errors,
            out List<ProbeNode> nodes, out int rectCount, out string truncReason)
        {
            nodes = new List<ProbeNode>();
            rectCount = 0;
            truncReason = null;

            var stack = new Stack<MsaaRef>();
            stack.Push(new MsaaRef { Owner = root.Accessible, ChildId = 0, Depth = 0 });

            const int MaxNodes = 3000;
            const int MaxDepth = 40;

            while (stack.Count > 0)
            {
                if (nodes.Count >= MaxNodes) { truncReason = "node_budget"; break; }
                MsaaRef cur = stack.Pop();
                if (cur.Depth > MaxDepth) { truncReason = truncReason ?? "depth_budget"; continue; }

                var d = Msaa.ReadNode(cur, errors);
                var n = new ProbeNode
                {
                    Name = d["name"] as string ?? "",
                    Role = d["role"] as string ?? "",
                    Rect = d["rect"] as int[],
                    HasDefaultAction = !string.IsNullOrEmpty(d["defaultAction"] as string ?? ""),
                    Depth = cur.Depth,
                };
                if (n.Rect != null) rectCount++;
                nodes.Add(n);

                int obtained;
                var kids = Msaa.ChildrenBatch(cur.Owner, 0, 64, out obtained, errors);
                for (int ci = kids.Count - 1; ci >= 0; ci--)
                {
                    kids[ci].Depth = cur.Depth + 1;
                    stack.Push(kids[ci]);
                }
            }
        }

        /* 找出真正的「内容 HWND」。
         *
         * ── 为什么需要这一步 ──
         *
         * 直接对顶层 Chrome_WidgetWin_1 取 OBJID_CLIENT，拿到的是**浏览器外壳**：
         * 菜单栏、工具栏、输入法按钮。实测 3000 个节点里 2 个矩形，全是
         * role=43 的"输入法" —— 完全不是网页内容。
         *
         * 网页在子窗口里，Chromium 的类名是 Chrome_RenderWidgetHostHWND
         * （旧版显示名是 "Chrome Legacy Window"，但**类名**才是判据）。
         * 一个窗口可能有多个（主页面 + 弹窗 + 嵌入内容），所以逐个试、
         * 取信息量最大的那个，而不是假定只有一个。
         */
        private static List<IntPtr> FindContentWindows(IntPtr root)
        {
            var found = new List<IntPtr>();
            Native.EnumChildWindows(root, delegate(IntPtr child, IntPtr lp)
            {
                var csb = new System.Text.StringBuilder(256);
                Native.GetClassNameW(child, csb, csb.Capacity);
                string cls = csb.ToString();
                // Chromium 内容宿主；Tauri / WebView2 也有各自的类名
                if (cls.IndexOf("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cls.IndexOf("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cls.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cls.IndexOf("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found.Add(child);
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        internal static Dictionary<string, object> Run(IntPtr hwnd)
        {
            var notes = new List<string>();
            var errors = new List<string>();
            var result = new Dictionary<string, object>();

            // ── 目标基本信息 ──
            var sb = new System.Text.StringBuilder(256);
            Native.GetClassNameW(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            result["hwnd"] = hwnd.ToInt64();
            result["pid"] = (int)pid;
            result["className"] = cls;

            // ── Chromium 辅助技术握手 ──
            //
            // 完整流程：挂 OUTOFCONTEXT 事件钩子 → 泵消息 → 收到
            // EVENT_SYSTEM_ALERT(idObject=1) 时用 AccessibleObjectFromEvent 响应
            // → 之后重新取树。**不注入 DLL，钩子只绑目标 PID。**
            var handshake = new Dictionary<string, object>();
            handshake["compatHr"] = "0x" + (Msaa.TryCompatHandshake(hwnd) & 0xFFFFFFFF).ToString("X8");
            int hsHr;
            var hsRoot = Msaa.OpenClient(hwnd, out hsHr);
            handshake["msaaRootHr"] = "0x" + (hsHr & 0xFFFFFFFF).ToString("X8");
            handshake["msaaRootOk"] = (hsRoot != null);

            // 握手前的节点数：拿它和握手后对比，才能说"握手有没有用"
            int beforeMsaa = 0, beforeRect = 0;
            if (hsRoot != null && hsHr >= 0)
            {
                List<ProbeNode> bn; int brc; string btr;
                WalkBounded(new IAccessibleShim(hsRoot), new List<string>(), out bn, out brc, out btr);
                beforeMsaa = bn.Count;
                beforeRect = brc;
            }
            handshake["msaaBefore"] = beforeMsaa;
            handshake["rectBefore"] = beforeRect;

            handshake["events"] = A11yHandshake.Run(hwnd, pid, 700);
            result["handshake"] = handshake;

            Thread.Sleep(250);

            // ── UIA：Raw View + 多次重查 ──
            result["uia"] = ProbeUia(hwnd, errors);

            // ── MSAA：先顶层，再逐个内容 HWND，取信息量最大的那个 ──
            //
            // 只试顶层是不够的：顶层 Chrome_WidgetWin_1 的 OBJID_CLIENT
            // 给的是浏览器外壳（菜单栏/工具栏/输入法），实测 3000 个节点
            // 只有 2 个矩形。网页在子窗口里。
            var msaa = new Dictionary<string, object>();
            var candidates = new List<IntPtr>();
            candidates.Add(hwnd);
            var contentHwnds = FindContentWindows(hwnd);
            foreach (var c in contentHwnds) candidates.Add(c);

            // 把所有子窗口的类名也报出来：找不到内容宿主时，这份名单
            // 是唯一能说明"为什么没找到"的东西。
            var childClasses = new List<string>();
            Native.EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr lp)
            {
                var sb2 = new System.Text.StringBuilder(256);
                Native.GetClassNameW(child, sb2, sb2.Capacity);
                string cn = sb2.ToString();
                if (childClasses.Count < 40 && !childClasses.Contains(cn)) childClasses.Add(cn);
                return true;
            }, IntPtr.Zero);
            result["childWindowClasses"] = childClasses;

            var tried = new List<Dictionary<string, object>>();
            int rootHr = unchecked((int)0x80004005);
            IAccessibleShim root = null;
            IntPtr bestHwnd = hwnd;
            int bestScore = -1;
            int bestNodeCount = 0;
            int bestRectCount = 0;
            List<ProbeNode> bestNodes = null;

            foreach (var cand in candidates)
            {
                int h;
                IAccessibleShim r;
                try { r = OpenMsaaRoot(cand, out h); }
                catch (Exception e) { h = unchecked((int)0x80004005); r = null; errors.Add("MSAA 根: " + e.Message); }

                var rec = new Dictionary<string, object>();
                rec["hwnd"] = cand.ToInt64();
                rec["hr"] = "0x" + (h & 0xFFFFFFFF).ToString("X8");
                if (r == null || h < 0)
                {
                    rec["nodeCount"] = 0;
                    rec["rectCount"] = 0;
                    tried.Add(rec);
                    continue;
                }

                List<ProbeNode> ns;
                int rc;
                string trunc;
                WalkBounded(r, errors, out ns, out rc, out trunc);

                rec["nodeCount"] = ns.Count;
                rec["rectCount"] = rc;
                rec["truncatedBy"] = trunc;
                tried.Add(rec);

                // 评分：**有矩形的数量**才算数。节点多但全是容器没有意义。
                int score = rc * 1000 + ns.Count;
                if (score > bestScore)
                {
                    bestScore = score;
                    rootHr = h;
                    root = r;
                    bestHwnd = cand;
                    bestNodeCount = ns.Count;
                    bestRectCount = rc;
                    bestNodes = ns;
                }
            }

            msaa["tried"] = tried;

            // 握手前后的对比：这是判断"握手有没有用"的唯一依据
            handshake["msaaAfter"] = bestNodeCount;
            handshake["rectAfter"] = bestRectCount;
            handshake["helped"] = (bestRectCount > beforeRect) || (bestNodeCount > beforeMsaa);
            msaa["chosenHwnd"] = bestHwnd.ToInt64();
            msaa["contentHwndCount"] = contentHwnds.Count;
            msaa["hr"] = "0x" + (rootHr & 0xFFFFFFFF).ToString("X8");
            msaa["hrOk"] = rootHr >= 0;
            msaa["hr"] = "0x" + (rootHr & 0xFFFFFFFF).ToString("X8");
            msaa["hrOk"] = rootHr >= 0;

            var nodes = bestNodes ?? new List<ProbeNode>();
            string truncReason = null;
            msaa["nodeCount"] = bestNodeCount;
            msaa["rectCount"] = bestRectCount;
            msaa["truncatedBy"] = null;

            // ── IA2 能力探测（只探不调）──
            var ia2 = new Dictionary<string, object>();
            if (root != null)
            {
                int ia2Hr;
                bool ia2Ok = Msaa.ProbeIA2(root.Accessible, out ia2Hr);
                ia2["available"] = ia2Ok;
                ia2["hr"] = "0x" + (ia2Hr & 0xFFFFFFFF).ToString("X8");
            }
            else
            {
                ia2["available"] = false;
                ia2["hr"] = "skipped";
            }
            msaa["ia2"] = ia2;
            result["msaa"] = msaa;

            // ── 覆盖率判断 ──
            //
            // 这是给人看的结论，不是给模型判断的。四种结果要能分开：
            //   none        两条通道都没拿到东西
            //   shell_only  只拿到外壳（UIA 只有 1-2 个节点、MSAA 也没矩形）
            //   partial     拿到了，但被预算截断或有错误
            //   full        拿到了，没截断、没错
            var uiaInfo = result["uia"] as Dictionary<string, object>;
            var uiaCounts = uiaInfo != null ? uiaInfo["nodeCounts"] as List<int> : null;
            int uiaNodes = 0, uiaFirst = 0;
            bool uiaGrew = false;
            if (uiaCounts != null && uiaCounts.Count > 0)
            {
                uiaFirst = uiaCounts[0];
                uiaNodes = uiaCounts[uiaCounts.Count - 1];
                uiaGrew = uiaNodes > uiaFirst;
            }
            result["uiaNodeCount"] = uiaNodes;

            int useful = 0;
            foreach (var n in nodes) if (n.Rect != null) useful++;

            string coverage;
            if (useful == 0 && uiaNodes <= 2) coverage = "none_or_shell_only";
            else if (truncReason != null || errors.Count > 0) coverage = "partial";
            else coverage = "full";

            result["coverage"] = coverage;

            if (coverage == "none_or_shell_only")
            {
                notes.Add("两条通道都没拿到有效矩形。可能是：应用关闭了可访问性、");
                notes.Add("内容是 canvas/WebGL、或者 provider 还没初始化。");
                notes.Add("可以稍后重试一次；重试仍为空再考虑视觉兜底。");
            }
            if (uiaGrew)
            {
                notes.Add(string.Format(
                    "UIA 从 {0} 个节点增长到 {1} 个 —— 这棵树是**异步建立**的。",
                    uiaFirst, uiaNodes));
                notes.Add("所以「第一次查只拿到壳」不代表没元素，要允许一次重查。");
            }
            if (bestRectCount > 0 && useful == 0)
                notes.Add("MSAA 有节点但没有有效矩形 —— 树存在，位置拿不到。");

            result["notes"] = notes;
            result["errors"] = errors;

            // sample 优先给**有矩形**的节点。
            //
            // 原来取前 40 个，实测 SuYou 有 71 个矩形全在深处，前 40 个里
            // 只有 2 个 —— 等于把有用的信息筛掉了。这个命令是给人调试用的，
            // 就该先给"能点的东西"。
            var interesting = new List<ProbeNode>();
            foreach (var n0 in nodes) if (n0.Rect != null) interesting.Add(n0);
            // 有矩形的太多时，再按"有名字/有默认动作"排前面
            interesting.Sort(delegate(ProbeNode a, ProbeNode b)
            {
                int sa = (a.HasDefaultAction ? 2 : 0) + (string.IsNullOrEmpty(a.Name) ? 0 : 1);
                int sbScore = (b.HasDefaultAction ? 2 : 0) + (string.IsNullOrEmpty(b.Name) ? 0 : 1);
                return sbScore.CompareTo(sa);
            });
            if (interesting.Count > 60) interesting.RemoveRange(60, interesting.Count - 60);
            // 再补几个没矩形的（有名无位置也有诊断价值）
            foreach (var n0 in nodes)
            {
                if (interesting.Count >= 80) break;
                if (n0.Rect == null && !string.IsNullOrEmpty(n0.Name)) interesting.Add(n0);
            }

            var sample = new List<Dictionary<string, object>>();
            foreach (var n in interesting)
            {
                sample.Add(new Dictionary<string, object>
                {
                    { "name", n.Name },
                    { "role", n.Role },
                    { "rect", n.Rect },
                    { "hasDefaultAction", n.HasDefaultAction },
                    { "depth", n.Depth },
                });
            }
            result["sample"] = sample;

            return result;
        }

        /// <summary>打开 MSAA 根；返回一个持有 IAccessible 的包装。</summary>
        private static IAccessibleShim OpenMsaaRoot(IntPtr hwnd, out int hr)
        {
            Accessibility.IAccessible root = Msaa.OpenClient(hwnd, out hr);
            if (root == null) return null;
            return new IAccessibleShim(root);
        }

        /* UIA 探测：Raw View、多次重查、打印前几个节点。
         *
         * ── 为什么要这么麻烦 ──
         *
         * Edge 155（Chrome 155 内核）实测：Control View 的 Descendants 只有
         * 2 个节点。但 Chrome 138 起 Windows 上默认启用**原生 UIA** ——
         * 拿不到多半是我们的查法或时机不对，而不是"没有元素"。
         *
         * 三个可能：① 树是异步建的，一次查太早；② Control View 被过滤了，
         * 要看 Raw View；③ Chromium 的 provider 需要辅助技术激活信号。
         * 所以这里把三次查询都记下来，让"没拿到"变成可诊断的。
         */
        private static Dictionary<string, object> ProbeUia(IntPtr hwnd, List<string> errors)
        {
            var info = new Dictionary<string, object>();
            var counts = new List<int>();
            string rootName = "";
            string rootClass = "";
            var firstFew = new List<Dictionary<string, object>>();

            try
            {
                var el = AutomationElement.FromHandle(hwnd);
                if (el == null)
                {
                    info["error"] = "FromHandle 返回 null";
                    return info;
                }
                try { rootName = el.Current.Name ?? ""; } catch { }
                try { rootClass = el.Current.ClassName ?? ""; } catch { }

                // 三次查询，间隔递增：120ms / 600ms / 1500ms
                int[] waits = new int[] { 120, 600, 1500 };
                var rawWalker = TreeWalker.RawViewWalker;
                for (int round = 0; round < waits.Length; round++)
                {
                    if (round > 0) Thread.Sleep(waits[round]);
                    int n;
                    try
                    {
                        var found = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                        n = found == null ? 0 : found.Count;
                    }
                    catch (Exception e) { errors.Add("UIA 第" + (round + 1) + "次: " + e.Message); n = 0; }
                    counts.Add(n);
                    if (n > 0) break;
                }

                // ── TreeWalker 递归（对比 FindAll）──
                //
                // FindAll(Descendants) 依赖 provider 实现；某些版本会返回很浅的
                // 结果而 TreeWalker 逐层 GetFirstChild 能走得更深。
                // 两条路都试，把数量都记下来 —— 差异本身就是诊断信息。
                int walkerTotal = 0;
                int walkerWithRect = 0;
                try
                {
                    var queue = new Queue<AutomationElement>();
                    queue.Enqueue(el);
                    int guard = 0;
                    while (queue.Count > 0 && guard < 5000)
                    {
                        guard++;
                        var cur2 = queue.Dequeue();
                        AutomationElement ch = null;
                        try { ch = rawWalker.GetFirstChild(cur2); } catch { }
                        int sib = 0;
                        while (ch != null && sib < 200)
                        {
                            sib++;
                            walkerTotal++;
                            try
                            {
                                var rr = ch.Current.BoundingRectangle;
                                if (rr.Width > 0 && rr.Height > 0) walkerWithRect++;
                            }
                            catch { }
                            queue.Enqueue(ch);
                            try { ch = rawWalker.GetNextSibling(ch); } catch { break; }
                        }
                    }
                }
                catch (Exception e) { errors.Add("UIA TreeWalker: " + e.Message); }
                info["walkerTotal"] = walkerTotal;
                info["walkerWithRect"] = walkerWithRect;

                // 前几个节点（用 Raw View 走一遍，看真实子树长什么样）
                try
                {
                    AutomationElement cur = el;
                    int guard = 0;
                    while (cur != null && firstFew.Count < 8 && guard < 40)
                    {
                        guard++;
                        var d = new Dictionary<string, object>();
                        try { d["name"] = cur.Current.Name ?? ""; } catch { d["name"] = ""; }
                        try { d["class"] = cur.Current.ClassName ?? ""; } catch { d["class"] = ""; }
                        try { d["type"] = cur.Current.ControlType.ProgrammaticName ?? ""; } catch { d["type"] = ""; }
                        try
                        {
                            var r = cur.Current.BoundingRectangle;
                            d["rect"] = (r.Width > 0 && r.Height > 0)
                                ? new int[] { (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height }
                                : null;
                        }
                        catch { d["rect"] = null; }
                        firstFew.Add(d);

                        AutomationElement child = null;
                        try { child = rawWalker.GetFirstChild(cur); } catch { }
                        cur = child;
                    }
                }
                catch (Exception e) { errors.Add("UIA 遍历: " + e.Message); }
            }
            catch (Exception e)
            {
                errors.Add("UIA: " + e.Message);
            }

            info["nodeCounts"] = counts;
            info["nodeCount"] = counts.Count > 0 ? counts[counts.Count - 1] : 0;
            info["nodeCountAfter"] = counts.Count > 1 ? counts[counts.Count - 1] : (counts.Count > 0 ? counts[0] : 0);
            info["rootName"] = rootName;
            info["rootClass"] = rootClass;
            info["firstDescendants"] = firstFew;
            return info;
        }
    }

    /// <summary>把 IAccessible 包一层，避免把 Accessibility 类型散到各处。</summary>
    internal sealed class IAccessibleShim
    {
        internal readonly Accessibility.IAccessible Accessible;
        internal IAccessibleShim(object raw) { Accessible = (Accessibility.IAccessible)raw; }
    }
}
