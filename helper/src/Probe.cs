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
        private static bool IsAction(int role)
            {
                switch (role)
                {
                    case 30:  // Link
                    case 34:  // ListItem（可选中）
                    case 37:  // PageTab
                    case 43:  // PushButton
                    case 44:  // CheckButton
                    case 45:  // RadioButton
                    case 46:  // ComboBox
                    case 47:  // DropList
                    case 52:  // SpinButton
                    case 56:  // ButtonDropDown
                    case 57:  // ButtonMenu
                    case 62:  // SplitButton
                        return true;
                    default:
                        return false;
                }
            }
        private static bool IsContent(int role)
            {
                switch (role)
                {
                    case 15:  // Document
                    case 28:  // Row
                    case 29:  // Cell
                    case 30:  // Link
                    case 33:  // List
                    case 34:  // ListItem
                    case 40:  // Graphic
                    case 41:  // StaticText
                    case 42:  // Text
                    case 43:  // PushButton
                    case 44:  // CheckButton
                    case 45:  // RadioButton
                    case 46:  // ComboBox
                    case 47:  // DropList
                    case 54:  // Animation
                    case 61:  // Clock
                        return true;
                    default:
                        return false;
                }
            }

        /// <summary>
        /// 有界遍历（薄适配层）。
        ///
        /// 真正的逻辑在 BoundedWalk.cs —— 它不碰 COM，所以能被 WalkSelfTest
        /// 用假数据检验。这里只负责把 IAccessible 包装成 BoundedWalk 要的
        /// 四个委托，并**如实保留**截断原因。
        ///
        /// 旧版本把遍历逻辑直接写在这里、和 COM 缠在一起，结果没法测 ——
        /// 外部复核用纯内存复现抓到三个确定的错误：
        ///   · 简单子元素的 Owner 是父对象，却无条件枚举它的孩子 → 重复递归
        ///   · 永远只取 start=0 的前 64 个孩子 → 第 65 个以后消失且不报截断
        ///   · 没有身份去重 → 环会膨胀
        /// </summary>
        private static WalkResult WalkBounded(
            IAccessibleShim root, List<string> errors, int maxNodes)
        {
            var identities = new Dictionary<object, string>();
            int counter = 0;

            // 给每个节点一个稳定身份。简单子元素 = "父身份#childId"，
            // 完整对象 = 分配一个序号（同一对象在本次遍历里复用同一个序号）。
            // **不能按 Name + Rect 去重** —— 不同按钮可能同名，
            // 也可能暂时拥有相同矩形。
            Func<object, string> identity = delegate(object o)
            {
                MsaaRef r = o as MsaaRef;
                if (r == null) return "x" + (counter++);
                if (r.SimpleChild)
                    return IdentityOf(r.Owner, identities, ref counter) + "#" + r.ChildId;
                return IdentityOf(r.Owner, identities, ref counter);
            };

            return BoundedWalk.Run(
                new MsaaRef { Owner = root.Accessible, ChildId = 0, Depth = 0 },
                delegate(object o)
                {
                    MsaaRef r = (MsaaRef)o;
                    var d = Msaa.ReadNode(r, errors);
                    return new WalkNode
                    {
                        Identity = identity(o),
                        Name = d["name"] as string ?? "",
                        Role = d["role"] as string ?? "",
                        Rect = d["rect"] as int[],
                        HasDefaultAction = !string.IsNullOrEmpty(d["defaultAction"] as string ?? ""),
                    };
                },
                delegate(object o, int startOff, int count)
                {
                    MsaaRef r = (MsaaRef)o;
                    var page = new WalkPage();
                    // ★ 枚举失败必须**传进 WalkPage**。
                    //
                    // 旧实现只把成功时的孩子复制过去，失败时 page 是空白的 ——
                    // 遍历器看到 Obtained=0 就当成"这一页空了 = 枚举结束"，
                    // 于是把失败报成 TraversalComplete=true。
                    // 外部复核用纯内存复现验证过这个契约。
                    var er = Msaa.ChildrenBatch(r.Owner, startOff, count, errors);
                    page.Obtained = er.Obtained;
                    page.Total = -1;
                    if (er.Failed) page.FailReason = er.FailReason;
                    if (er.Children != null)
                        foreach (var k in er.Children) page.Children.Add(k);
                    return page;
                },
                delegate(object o) { return ((MsaaRef)o).SimpleChild; },
                maxNodes,
                40);
        }

        /** WalkResult → 旧的 ProbeNode 列表（下游代码还在用这个类型）。 */
        private static List<ProbeNode> ToProbeNodes(WalkResult wr)
        {
            var list = new List<ProbeNode>();
            for (int i = 0; i < wr.Nodes.Count; i++)
            {
                list.Add(new ProbeNode
                {
                    Name = wr.Nodes[i].Name,
                    Role = wr.Nodes[i].Role,
                    Rect = wr.Nodes[i].Rect,
                    HasDefaultAction = wr.Nodes[i].HasDefaultAction,
                    Depth = wr.Depths[i],
                });
            }
            return list;
        }

        /// <summary>给一个 IAccessible 分配稳定身份（本次遍历内）。</summary>
        private static string IdentityOf(
            Accessibility.IAccessible obj, Dictionary<object, string> map, ref int counter)
        {
            if (obj == null) return "null";
            string id;
            if (map.TryGetValue(obj, out id)) return id;
            id = "o" + (counter++);
            map[obj] = id;
            return id;
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
                WalkResult bw = WalkBounded(
                    new IAccessibleShim(hsRoot), new List<string>(), 800);
                beforeMsaa = bw.UniqueCount;
                beforeRect = bw.RectCount;
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
            string bestTrunc = null;
            bool bestComplete = false;
            int bestDup = 0;

            foreach (var cand in candidates)
            {
                int h;
                IAccessibleShim r;
                try { r = OpenMsaaRoot(cand, out h); }
                catch (Exception e) { h = unchecked((int)0x80004005); r = null; errors.Add("MSAA 根: " + e.Message); }

                var rec = new Dictionary<string, object>();
                rec["hwnd"] = cand.ToInt64();
                {
                    var csb2 = new System.Text.StringBuilder(256);
                    Native.GetClassNameW(cand, csb2, csb2.Capacity);
                    rec["className"] = csb2.ToString();
                    uint cpid;
                    Native.GetWindowThreadProcessId(cand, out cpid);
                    rec["pid"] = (int)cpid;
                    var hsp = Msaa.HasServerSideProvider(cand);
                    rec["uiaServerProvider"] = hsp;
                }
                rec["hr"] = "0x" + (h & 0xFFFFFFFF).ToString("X8");
                if (r == null || h < 0)
                {
                    rec["nodeCount"] = 0;
                    rec["rectCount"] = 0;
                    tried.Add(rec);
                    continue;
                }

                WalkResult wr = WalkBounded(r, errors, 3000);
                List<ProbeNode> ns = ToProbeNodes(wr);
                int rc = wr.RectCount;
                string trunc = wr.TruncatedBy;

                rec["nodeCount"] = ns.Count;
                rec["rectCount"] = rc;
                rec["truncatedBy"] = trunc;
                rec["traversalComplete"] = wr.TraversalComplete;
                rec["duplicateEdges"] = wr.DuplicateEdges;
                // ★ 这两个不是一回事：
                //   traversalComplete = 当次可达范围走完了
                //   contentStatus    = 有没有摸到"像内容"的东西
                rec["contentStatus"] = rc > 0 ? "has_rects" : "shell_only";
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
                    bestTrunc = trunc;
                    bestComplete = wr.TraversalComplete;
                    bestDup = wr.DuplicateEdges;
                }
            }

            msaa["tried"] = tried;

            // ── 每个候选的独立诊断（不按矩形数淘汰）──
            //
            // 外部复核："内容根不能再因矩形少而被跳过"。所以顶层和每个
            // 内容窗口都记一份：根角色/名称/childCount、第一页是对象还是
            // 整数孩子、整数孩子能不能 get_accChild 拿到独立对象、IA2。
            var perCandidate = new List<Dictionary<string, object>>();
            foreach (var c in candidates)
            {
                try { perCandidate.Add(PointQuery.DiagnoseCandidate(c, errors)); }
                catch (Exception e) { errors.Add("候选诊断 " + c.ToInt64() + ": " + e.Message); }
            }
            msaa["candidates"] = perCandidate;

            // ── 点查询 ──
            //
            // 外部复核对这条的判据：**点查询得到细元素而根遍历得不到 →
            // 优先修导航入口**；两者都只有外壳才算真的没有内容。
            // 它比"根遍历有没有元素"硬，因为它绕过了我的遍历代码。
            Native.RECT wrect;
            if (Native.GetWindowRect(hwnd, out wrect))
            {
                try
                {
                    int w = wrect.Right - wrect.Left;
                    int h = wrect.Bottom - wrect.Top;
                    result["pointQuery"] = PointQuery.Run(hwnd, wrect.Left, wrect.Top, w, h, errors);
                }
                catch (Exception e) { errors.Add("点查询: " + e.Message); }
            }


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

            // ── 保留选中候选的真实截断原因 ──
            //
            // 旧代码在这里把 truncReason 设成 null、把 truncatedBy 输出 null，
            // 然后靠"有矩形且无错误"给出 coverage=full —— 外壳也能被标成完整。
            // 外部复核点名了这两处（Probe.cs 335/338、381-382）。
            var nodes = bestNodes ?? new List<ProbeNode>();
            string truncReason = bestTrunc;
            msaa["nodeCount"] = bestNodeCount;
            msaa["rectCount"] = bestRectCount;
            msaa["truncatedBy"] = bestTrunc;
            msaa["traversalComplete"] = bestComplete;
            msaa["duplicateEdges"] = bestDup;

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

            // ── coverage 不能再说"full" ──
            //
            // ── 数值表修过一次错 ──
            //
            // 上一版注释写 "42=LINK、41=TEXT"，**两个都错了**。
            // 官方 AccessibleRole 枚举是：
            //
            //   Link       30      StaticText 41      Text       42
            //   ListItem   34      PushButton 43      CheckButton 44
            //   RadioButton 45     ComboBox   46      DropList   47
            //
            // 用错表的后果很具体：**真正的链接漏计，而静态文字反而被算成
            // 可交互** —— 只有链接的页面会被说成 shell_only，只有静态文字的
            // 页面会被说成 has_candidates。两个方向都错。
            //
            // ── 而且这两个概念要分开 ──
            //
            //   contentEvidence : 这块区域**有内容**（文字、列表项、图形都算）
            //   actionCandidates: 有**可操作的**元素（按钮、链接、复选框…）
            //
            // "没有 actionCandidates"不能推出"只是外壳" —— 一段纯文字页面
            // 也有内容。
            int contentEvidence = 0;
            int actionCandidates = 0;
            foreach (var n in nodes)
            {
                if (n.Rect == null) continue;
                int role;
                if (!int.TryParse(n.Role, out role)) continue;
                if (IsAction(role)) actionCandidates++;
                if (IsContent(role)) contentEvidence++;
            }

            // coverage 三档，但判据换了：
            //   外壳 = 有矩形、但**一个内容证据都没有**（只有容器/标题栏/菜单）
            //   有内容 = 至少有一个内容证据；其中有没有可操作元素另记
            string coverage;
            if (useful == 0) coverage = "no_content";
            else if (contentEvidence == 0) coverage = "shell_only";
            else coverage = "has_content";
            result["contentEvidence"] = contentEvidence;
            result["actionCandidates"] = actionCandidates;
            result["traversalComplete"] = bestComplete;
            result["truncatedBy"] = bestTrunc;

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
                //
                // ★ 每轮**重新取得根**，不是复用第一轮的 el。
                //
                // 外部复核指出：旧的退出条件 `if (n > 0) break` 让"读到 2 个
                // 外壳节点"就退出了 —— 我文档里写的"三轮重查"实际只查了一轮。
                // 而 n>0 根本不是成功的标准，外壳也有节点。
                //
                // 现在固定跑完三轮，每轮记：根的名字/类名/节点数/有矩形数。
                // 这三组数字放在一起才有诊断价值 —— 只看一个总数看不出树在长。
                int[] waits = new int[] { 0, 600, 1500 };
                var rawWalker = TreeWalker.RawViewWalker;
                var rounds = new List<Dictionary<string, object>>();
                for (int round = 0; round < waits.Length; round++)
                {
                    if (waits[round] > 0) Thread.Sleep(waits[round]);
                    var rd = new Dictionary<string, object>();
                    rd["atMs"] = waits[round];
                    try
                    {
                        var root2 = AutomationElement.FromHandle(hwnd);
                        if (root2 == null)
                        {
                            rd["error"] = "FromHandle null";
                            rounds.Add(rd);
                            counts.Add(0);
                            continue;
                        }
                        try { rd["rootName"] = root2.Current.Name ?? ""; } catch { rd["rootName"] = ""; }
                        try { rd["rootClass"] = root2.Current.ClassName ?? ""; } catch { rd["rootClass"] = ""; }
                        // 托管 AutomationElement 没有 ProviderDescription ——
                        // 那个属性只存在于原生的 IUIAutomationElement
                        // （get_CurrentProviderDescription）。要拿到它得写完整的
                        // COM 声明，这一版先缺着，用 FrameworkId 代替。
                        // 这是已知缺口，不是"拿不到就说明没问题"。
                        rd["provider"] = "(unavailable: managed API has no ProviderDescription)";
                        try { rd["framework"] = root2.Current.FrameworkId ?? ""; } catch { rd["framework"] = ""; }
                        try { rd["processId"] = root2.Current.ProcessId; } catch { rd["processId"] = 0; }
                        try { rd["nativeHwnd"] = root2.Current.NativeWindowHandle; } catch { rd["nativeHwnd"] = 0; }

                        // ── 先读"内容属性"，这一步是触发 Chromium 提升模式的关键 ──
                        //
                        // 外部复核的原话：Chromium 按**实际 API 使用**提升无障碍
                        // 模式 —— 读窗口名只给 kNativeAPIs；**读网页内容属性才给
                        // kAXModeBasic**。
                        //
                        // 而实测两个目标的 framework 都是 Win32（系统代理），
                        // 说明 Chromium 自己的 provider 根本没挂上。所以这里先
                        // 主动读一轮内容属性，看它会不会因此建树。
                        int contentElems = 0, controlElems = 0, pattCount = 0;
                        try
                        {
                            var desc = root2.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                            int lim = desc == null ? 0 : Math.Min(desc.Count, 60);
                            for (int i = 0; i < lim; i++)
                            {
                                try { if (desc[i].Current.IsContentElement) contentElems++; } catch { }
                                try { if (desc[i].Current.IsControlElement) controlElems++; } catch { }
                                try
                                {
                                    var pats = desc[i].GetSupportedPatterns();
                                    if (pats != null && pats.Length > 0) pattCount++;
                                }
                                catch { }
                            }
                        }
                        catch (Exception e) { rd["contentProbeError"] = e.Message; }
                        rd["contentElements"] = contentElems;
                        rd["controlElements"] = controlElems;
                        rd["nodeWithPatterns"] = pattCount;

                        var found = root2.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                        int n = found == null ? 0 : found.Count;
                        int withRect = 0;
                        if (found != null)
                        {
                            for (int i = 0; i < found.Count && i < 400; i++)
                            {
                                try
                                {
                                    var r = found[i].Current.BoundingRectangle;
                                    if (r.Width > 0 && r.Height > 0) withRect++;
                                }
                                catch { }
                            }
                        }
                        rd["nodeCount"] = n;
                        rd["withRect"] = withRect;
                        counts.Add(n);
                    }
                    catch (Exception e)
                    {
                        rd["error"] = e.Message;
                        counts.Add(0);
                        errors.Add("UIA 第" + (round + 1) + "轮: " + e.Message);
                    }
                    rounds.Add(rd);
                }
                info["rounds"] = rounds;

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
