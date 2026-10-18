/* 点查询与逐候选诊断。
 *
 * ── 为什么需要点查询 ──
 *
 * 外部复核给了一条判据："若点查询得到细元素而根遍历得不到，优先修导航入口；
 * 若两者都只返回系统外壳，才进一步查环境和 provider。"
 *
 * 这条比"根遍历有没有元素"更硬：它绕过了我们的遍历代码，直接问系统
 * "这个像素下面是什么"，所以不会因为我的遍历器有 bug 而误判。
 *
 * ── 两个入口 ──
 *
 *   AccessibleObjectFromPoint  → MSAA（返回 IAccessible + child）
 *   AutomationElement.FromPoint → UIA（返回 AutomationElement）
 *
 * 都在**只读**范围内：只查，不移动鼠标、不点击。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Automation;

namespace Dsc
{
    internal static class PointQuery
    {
        [System.Runtime.InteropServices.DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromPoint(
            Native.POINT pt,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)]
            out Accessibility.IAccessible ppacc,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Struct)]
            out object pvarChild);

        /// <summary>
        /// 在若干个点上分别用 MSAA 和 UIA 查"这里是什么"。
        ///
        /// ── 一个必须记进结果的前提 ──
        ///
        /// 点查询问的是"**这个像素**下面是谁"，而像素不属于某个窗口 ——
        /// 被遮挡时问到的是上面那个窗口。实测：WinForms 阳性对照被 Edge
        /// 盖住，点查询返回的是 Edge 的 'Chrome Legacy Window'。
        ///
        /// 所以结果里必须带上"查询时目标窗口是不是前台"这个事实，
        /// 否则那行数据会被读成"这个窗口的内部元素"。
        /// </summary>
        internal static List<Dictionary<string, object>> Run(
            IntPtr hwnd, int originX, int originY, int width, int height, List<string> errors)
        {
            var outList = new List<Dictionary<string, object>>();

            bool foreground = Native.GetForegroundWindow() == hwnd;
            if (!foreground)
            {
                errors.Add(
                    "点查询时目标窗口不是前台 —— 采样点可能落在别的窗口上，" +
                    "这一组数据不能用来判断该目标暴露了什么");
            }

            // 采样点：内容区的中心 + 几个偏移。不是在边框上取点，
            // 因为边框通常属于窗口外壳。
            int cx = originX + width / 2;
            int cy = originY + height / 2;
            var pts = new int[][]
            {
                new int[] { cx, cy },
                new int[] { cx - width / 4, cy - height / 4 },
                new int[] { cx + width / 4, cy + height / 4 },
                new int[] { cx, cy - height / 3 },
            };

            foreach (var p in pts)
            {
                var rec = new Dictionary<string, object>();
                rec["x"] = p[0];
                rec["y"] = p[1];
                rec["targetWasForeground"] = foreground;
                // 这个点下面到底是哪个窗口：明确回答"落点归属"，
                // 而不是让读者从 MSAA 的名字去猜
                IntPtr under = Native.WindowFromPoint(new Native.POINT { X = p[0], Y = p[1] });
                rec["windowUnderPoint"] = under.ToInt64();
                // WindowFromPoint 返回的常常是**子窗口**（控件），不是顶层。
                // 所以要比对根窗口，不能直接比 hwnd —— 我第一版就是这么错的，
                // 结果一个点落在 Beta 按钮上、读到了 'Beta'，却报「不属于目标」。
                IntPtr rootOfUnder = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, Native.GA_ROOT);
                rec["pointBelongsToTarget"] = (rootOfUnder != IntPtr.Zero && rootOfUnder == hwnd);

                // ── MSAA ──
                try
                {
                    Native.POINT pt = new Native.POINT();
                    pt.X = p[0];
                    pt.Y = p[1];
                    Accessibility.IAccessible acc;
                    object child;
                    int hr = AccessibleObjectFromPoint(pt, out acc, out child);
                    if (hr >= 0 && acc != null)
                    {
                        object c = child;
                        string nm = "";
                        string role = "";
                        int x = 0, y = 0, w = 0, h = 0;
                        try { nm = acc.get_accName(c) ?? ""; } catch { }
                        try { object r = acc.get_accRole(c); role = r == null ? "" : r.ToString(); } catch { }
                        try { acc.accLocation(out x, out y, out w, out h, c); } catch { }
                        rec["msaa"] = new Dictionary<string, object>
                        {
                            { "hr", "0x" + (hr & 0xFFFFFFFF).ToString("X8") },
                            { "name", nm },
                            { "role", role },
                            { "rect", (w > 0 && h > 0) ? new int[] { x, y, w, h } : null },
                        };
                    }
                    else
                    {
                        rec["msaa"] = new Dictionary<string, object>
                        {
                            { "hr", "0x" + (hr & 0xFFFFFFFF).ToString("X8") },
                            { "name", "" }, { "role", "" }, { "rect", null },
                        };
                    }
                }
                catch (Exception e)
                {
                    rec["msaa"] = new Dictionary<string, object>
                    {
                        { "error", e.GetType().Name }, { "message", e.Message },
                    };
                    errors.Add("FromPoint(MSAA) " + p[0] + "," + p[1] + ": " + e.Message);
                }

                // ── UIA ──
                try
                {
                    var el = AutomationElement.FromPoint(new System.Windows.Point(p[0], p[1]));
                    if (el != null)
                    {
                        var d = new Dictionary<string, object>();
                        try { d["name"] = el.Current.Name ?? ""; } catch { d["name"] = ""; }
                        try { d["class"] = el.Current.ClassName ?? ""; } catch { d["class"] = ""; }
                        try { d["type"] = el.Current.ControlType.ProgrammaticName ?? ""; } catch { d["type"] = ""; }
                        try { d["framework"] = el.Current.FrameworkId ?? ""; } catch { d["framework"] = ""; }
                        try
                        {
                            var r = el.Current.BoundingRectangle;
                            d["rect"] = (r.Width > 0 && r.Height > 0)
                                ? new int[] { (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height }
                                : null;
                        }
                        catch { d["rect"] = null; }
                        rec["uia"] = d;
                    }
                    else
                    {
                        rec["uia"] = new Dictionary<string, object> { { "name", "(null element)" } };
                    }
                }
                catch (Exception e)
                {
                    rec["uia"] = new Dictionary<string, object>
                    {
                        { "error", e.GetType().Name }, { "message", e.Message },
                    };
                    errors.Add("FromPoint(UIA) " + p[0] + "," + p[1] + ": " + e.Message);
                }

                outList.Add(rec);
            }
            return outList;
        }

        /// <summary>
        /// 给每个候选做独立诊断：根角色/名称、childCount、IA2、整数孩子。
        ///
        /// **不按矩形数量淘汰内容根。** 外部复核指出旧逻辑用矩形数选"最佳根"，
        /// 而重复的外壳可能胜出、内容根因为矩形少被跳过。这里每个候选都记。
        /// </summary>
        internal static Dictionary<string, object> DiagnoseCandidate(
            IntPtr hwnd, List<string> errors)
        {
            var d = new Dictionary<string, object>();
            d["hwnd"] = hwnd.ToInt64();

            var sb = new System.Text.StringBuilder(256);
            Native.GetClassNameW(hwnd, sb, sb.Capacity);
            d["className"] = sb.ToString();
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            d["pid"] = (int)pid;
            d["uiaServerProvider"] = Msaa.HasServerSideProvider(hwnd);

            int hr;
            Accessibility.IAccessible root = Msaa.OpenClient(hwnd, out hr);
            d["rootHr"] = "0x" + (hr & 0xFFFFFFFF).ToString("X8");
            if (root == null || hr < 0) return d;

            // 根的角色 / 名称 / 孩子数
            try { d["rootName"] = root.get_accName(0) ?? ""; } catch { d["rootName"] = ""; }
            try { object r = root.get_accRole(0); d["rootRole"] = r == null ? "" : r.ToString(); }
            catch { d["rootRole"] = ""; }
            try { d["childCount"] = root.accChildCount; } catch { d["childCount"] = -1; }

            // 第一页孩子：对象 vs 整数（这个区分是关键）
            int objectKids = 0, intKids = 0;
            var firstIds = new List<object>();
            var er = Msaa.ChildrenBatch(root, 0, 16, errors);
            if (!er.Failed)
            {
                foreach (var k in er.Children)
                {
                    if (k.SimpleChild)
                    {
                        intKids++;
                        if (firstIds.Count < 8) firstIds.Add(k.ChildId);
                    }
                    else
                    {
                        objectKids++;
                    }
                }
            }
            d["firstPageObjects"] = objectKids;
            d["firstPageInts"] = intKids;
            d["firstPageIds"] = firstIds;
            d["firstPageFailed"] = er.Failed;
            d["firstPageFailReason"] = er.FailReason ?? "";

            // 整数孩子的追问：get_accChild 能不能拿到独立对象
            //
            // 外部复核要求的：对整数孩子按协议用 get_accChild(childId) 判断，
            // 拿得到就继续往下走，拿不到就确认它只是简单子元素。
            int promoted = 0;
            foreach (var id in firstIds)
            {
                if (!(id is int)) continue;
                try
                {
                    object full = root.get_accChild((int)id);
                    if (full is Accessibility.IAccessible) promoted++;
                }
                catch { }
            }
            d["intKidsWithOwnObject"] = promoted;

            // IA2 能力（每个候选都查，不只是选中的那个）
            int ia2Hr;
            bool ia2 = Msaa.ProbeIA2(root, out ia2Hr);
            d["ia2"] = new Dictionary<string, object>
            {
                { "available", ia2 },
                { "hr", "0x" + (ia2Hr & 0xFFFFFFFF).ToString("X8") },
            };

            return d;
        }
    }
}
