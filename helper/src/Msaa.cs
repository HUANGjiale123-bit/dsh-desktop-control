/* MSAA（IAccessible）只读后端。
 *
 * ── 为什么需要它 ──
 *
 * 之前这个项目的一个判断是"Electron/Chromium 在 UIA 里只露
 * Chrome Legacy Window 一层，所以只能按坐标点"。**那个判断是错的**：
 *
 *   · Chrome Legacy Window 是**中间窗口的名字**，不代表它只有一个节点；
 *     Chromium 的 LegacyRenderWidgetHostHWND::OnGetObject 同时处理
 *     UIA 与 MSAA，OBJID_CLIENT 会取到网页内容的 accessibility root。
 *   · Chrome 138 起 Windows 上默认启用原生 UIA；更早是经 MSAA 转换层。
 *     Electron 绑定自己的 Chromium 版本，所以不能拿本机 Chrome 的版本代替它。
 *   · Electron 官方：检测到辅助技术时会**自动启用**无障碍；
 *     启动参数只是强制启用的一种方式，不是唯一入口。
 *
 * 所以正确的做法是**能力探测**而不是按框架名一刀切：先 UIA，拿不到有效
 * 内容再走 MSAA，必要时探测 IA2。
 *
 * ── 这份文件只读 ──
 *
 * 不调用任何输入动作、不改目标设置。名字、角色、状态、矩形、默认动作
 * 描述都只用于**生成候选**，不伪造 clickable=true。
 *
 * ── 几个容易踩的点（来自外部复核，我保留在这里免得以后忘）────
 *
 *   · start 是**枚举偏移**，不是 childId；真实 childId 要传回父对象的
 *     属性方法（不能把数组下标当 childId）。
 *   · S_FALSE 仍可能带有效结果 —— 不能因为它不是 S_OK 就丢数据。
 *   · 简单子元素是 (父, childId)，对象子元素是 (它自己, CHILDID_SELF)；
 *     把两者都强转成 IAccessible 会**静默漏节点**。
 *   · accLocation 给的已经是**屏幕物理像素**，不能再加窗口原点或再乘 DPI。
 *   · 失败要传播：缺矩形、取名字失败都不能伪装成"空树"或"有效矩形"。
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Accessibility;

namespace Dsc
{
    /// <summary>一个 MSAA 节点引用：要么是对象自己，要么是父对象的某个简单子元素。</summary>
    internal sealed class MsaaRef
    {
        public IAccessible Owner;
        public int ChildId;          // CHILDID_SELF = 0，或 provider 返回的真实 childId
        public bool SimpleChild;
        public int Depth;
    }

    internal static class Msaa
    {
        private const uint OBJID_CLIENT = 0xFFFFFFFC;   // -4
        private const int CHILDID_SELF = 0;
        private static readonly Guid IidAccessible =
            new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        private static readonly Guid IidIA2 =
            new Guid("E89F726E-C4F4-4C19-BB19-B647D7FA8478");

        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd, uint objectId, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);

        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int AccessibleChildren(
            [MarshalAs(UnmanagedType.Interface)] IAccessible parent,
            int start, int count,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2,
                ArraySubType = UnmanagedType.Struct)] object[] children,
            out int obtained);

        // ── 取内容根 ──

        /// <summary>取 MSAA 内容根。失败时**抛异常**（不返回 null 假装成功）。</summary>
        internal static IAccessible OpenClient(IntPtr hwnd, out int hr)
        {
            Guid iid = IidAccessible;
            IAccessible root;
            hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out root);
            if (hr < 0) return null;
            return root;
        }

        /// <summary>
        /// 用任意对象 ID 取 IAccessible —— **只用于 Chromium 的辅助技术握手**。
        ///
        /// 对象 ID 1 是 Chromium 历史兼容的对象 ID。外部复核的原话：
        /// "对已确认的 Chromium 内容 HWND，也可以把 AccessibleObjectFromWindow(hwnd, 1, ...)
        ///  作为版本相关的兼容探测；不能当作 Windows 通用开关"。
        ///
        /// 注意：**返回失败不代表握手没触发** —— Chromium 那个分支可以处理请求
        /// 但不返回对象。所以调用方只能把 hr 记进诊断，不能据此判定。
        /// </summary>
        internal static int TryCompatHandshake(IntPtr hwnd)
        {
            Guid iid = IidAccessible;
            IAccessible obj;
            try
            {
                return AccessibleObjectFromWindow(hwnd, 1, ref iid, out obj);
            }
            catch (Exception)
            {
                return unchecked((int)0x80004005);
            }
        }

        [DllImport("UIAutomationCore.dll")]
        private static extern bool UiaHasServerSideProvider(IntPtr hwnd);

        /// <summary>
        /// 这个窗口有没有**服务端 UIA 实现**。
        ///
        /// 外部复核建议的诊断：它能区分"应用自己实现了 UIA provider"和
        /// "系统用 MSAA 转换层代理"。后者在 FrameworkId 上显示为 Win32。
        ///
        /// 注意：它只回答"有没有服务端实现"，**不保证有网页内容**，
        /// 也不能单独作为排除 MSAA 的依据。
        /// </summary>
        internal static Dictionary<string, object> HasServerSideProvider(IntPtr hwnd)
        {
            // ── 三态，不是真假 ──
            //
            // 旧实现 `catch (Exception) { return false; }` 把"API 返回 false"
            // 和"调用抛异常"混成一个字：False。外部复核指出这一点之后，
            // 我意识到那个 False 根本不能当证据用。
            //
            // 注意：不假装这个 BOOL API 有 HRESULT 成败语义，
            // 也不假定 GetLastError 一定有文档保证的含义。
            var d = new Dictionary<string, object>();
            try
            {
                bool v = UiaHasServerSideProvider(hwnd);
                d["status"] = "returned";
                d["value"] = v;
            }
            catch (Exception e)
            {
                d["status"] = "exception";
                d["value"] = null;
                d["exception"] = e.GetType().Name;
                d["hresult"] = "0x" + (e.HResult & 0xFFFFFFFF).ToString("X8");
                d["message"] = e.Message;
            }
            return d;
        }

        /// <summary>读一个节点的名字/角色/状态/默认动作/矩形。</summary>
        internal static Dictionary<string, object> ReadNode(MsaaRef node, List<string> errors)
        {
            object child = node.ChildId == CHILDID_SELF ? (object)CHILDID_SELF : (object)node.ChildId;
            var d = new Dictionary<string, object>();

            string name = null;
            try { name = node.Owner.get_accName(child); }
            catch (Exception e) { errors.Add("accName: " + e.Message); }
            d["name"] = name ?? "";

            object role = null;
            try { role = node.Owner.get_accRole(child); }
            catch (Exception e) { errors.Add("accRole: " + e.Message); }
            d["role"] = DescribeInt32(role);

            object state = null;
            try { state = node.Owner.get_accState(child); }
            catch (Exception e) { errors.Add("accState: " + e.Message); }
            d["state"] = DescribeInt32(state);

            string defaultAction = null;
            try { defaultAction = node.Owner.get_accDefaultAction(child); }
            catch { /* 很多元素没有默认动作，这不是错误 */ }
            d["defaultAction"] = defaultAction ?? "";

            // ── 矩形 ──
            //
            // accLocation 返回的是**屏幕物理像素**。文档明确如此，所以这里
            // 不再加窗口原点、也不乘 DPI。失败时把 rect 留成 null，
            // 让上层知道"这个节点没有可用矩形"，而不是给个 0,0,0,0。
            int x = 0, y = 0, w = 0, h = 0;
            bool haveRect = false;
            try
            {
                node.Owner.accLocation(out x, out y, out w, out h, child);
                haveRect = w > 0 && h > 0;
            }
            catch (Exception e) { errors.Add("accLocation: " + e.Message); }
            d["rect"] = haveRect ? new int[] { x, y, w, h } : null;

            return d;
        }

        /// <summary>把 VARIANT 里的整数（role/state）转成稳定的字符串表示。</summary>
        private static string DescribeInt32(object v)
        {
            if (v == null) return "";
            try
            {
                if (v is int) return ((int)v).ToString();
                return Convert.ToInt32(v).ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 分批枚举子节点。
        ///
        /// start 是**枚举偏移**，不是 childId。这是最容易搞错的一处：
        /// 把下标当 childId 传回父对象会读到别的元素，而且不会报错。
        /// </summary>
        /// <summary>
        /// 一次枚举的完整结果。
        ///
        /// ── 为什么不能只返回列表 ──
        ///
        /// 外部复核指出：旧实现失败时只往 errors 加一句字串、返回空列表，
        /// 而调用方把"空列表"当成"正常枚举结束" —— 于是遍历器报
        /// TraversalComplete=true，实际是失败被吞了。
        ///
        /// 他这个判断用纯内存复现验证过：模拟失败契约得到
        /// `errors=1, TraversalComplete=true, TruncatedBy=null`。
        /// **"走完"这个标志因此不可靠。**
        /// </summary>
        internal sealed class EnumResult
        {
            public List<MsaaRef> Children = new List<MsaaRef>();
            public int Obtained;
            public int Hr;                 // AccessibleChildren 的原始返回值
            public bool Failed;            // 真正的失败（hr < 0 或非法数量）
            public string FailReason;      // 人看的失败原因，非空即失败
        }

        internal static EnumResult ChildrenBatch(
            IAccessible parent, int start, int count, List<string> errors)
        {
            var r = new EnumResult();
            if (start < 0 || count < 1 || count > 64)
            {
                r.Failed = true;
                r.FailReason = "bad_range";
                return r;
            }

            object[] values = new object[count];
            int obtained;
            int hr;
            try
            {
                hr = AccessibleChildren(parent, start, count, values, out obtained);
            }
            catch (Exception e)
            {
                r.Failed = true;
                r.FailReason = "exception:" + e.GetType().Name;
                r.Hr = unchecked((int)0x80004005);
                return r;
            }
            r.Hr = hr;
            r.Obtained = obtained;

            // S_FALSE（=1）表示"拿到了但没拿满"，**仍然有有效数据**。
            // 只有真正的失败（hr < 0）才该放弃。
            if (hr < 0)
            {
                r.Failed = true;
                r.FailReason = "hr=0x" + hr.ToString("X8");
                r.Obtained = 0;
                errors.Add("AccessibleChildren " + r.FailReason);
                return r;
            }
            if (obtained < 0 || obtained > count)
            {
                r.Failed = true;
                r.FailReason = "bad_obtained:" + obtained;
                r.Obtained = 0;
                errors.Add("AccessibleChildren 返回了非法数量 " + obtained);
                return r;
            }
            var result = r.Children;

            for (int i = 0; i < obtained; i++)
            {
                IAccessible full = values[i] as IAccessible;
                if (full != null)
                {
                    result.Add(new MsaaRef { Owner = full, ChildId = CHILDID_SELF });
                    continue;
                }
                if (values[i] is int)
                {
                    result.Add(new MsaaRef
                    {
                        Owner = parent,
                        ChildId = (int)values[i],
                        SimpleChild = true,
                    });
                    continue;
                }
                // 混入未知类型时不能静默跳过 —— 那会让"树不完整"看起来像"树就这么大"。
                errors.Add("未知的 MSAA 子元素类型: " +
                    (values[i] == null ? "null" : values[i].GetType().Name));
            }
            return r;
        }

        /// <summary>子节点数量。provider 卡住时这里也可能抛，交给上层超时。</summary>
        internal static int ChildCount(IAccessible parent, List<string> errors)
        {
            try { return parent.accChildCount; }
            catch (Exception e)
            {
                errors.Add("accChildCount: " + e.Message);
                return 0;
            }
        }

        // ── IA2 能力探测 ──

        [ComImport]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProviderRaw
        {
            [PreserveSig]
            int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
        }

        /// <summary>
        /// 只用标准路径探测 IA2 是否可用：
        ///   IAccessible → QueryInterface(IServiceProvider) → QueryService(IAccessible, IA2)
        ///
        /// 不做任何 IA2 调用 —— 真正的 IA2 客户端需要完整的 IDL 生成声明，
        /// 手写精简接口去调 vtable 会因为接口顺序和封送出错而崩。
        /// </summary>
        internal static bool ProbeIA2(IAccessible accessible, out int hr)
        {
            hr = unchecked((int)0x80004002); // E_NOINTERFACE
            if (accessible == null) return false;
            IServiceProviderRaw sp = accessible as IServiceProviderRaw;
            if (sp == null) return false;

            Guid sid = IidAccessible;
            Guid iid = IidIA2;
            IntPtr ptr = IntPtr.Zero;
            try
            {
                hr = sp.QueryService(ref sid, ref iid, out ptr);
                return hr >= 0 && ptr != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (ptr != IntPtr.Zero) { try { Marshal.Release(ptr); } catch { } }
            }
        }
    }
}
