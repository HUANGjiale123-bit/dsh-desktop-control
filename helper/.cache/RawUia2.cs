// 原生 IUIAutomation 的 FindAll —— 用正确的 COM 接口类型。
//
// ── 上一版错在哪 ──
//
// 我把 FindAll 的第二个参数写成 IntPtr，而它实际要的是
// IUIAutomationCondition 接口指针。签名一错就是 NullReferenceException
// 或者更糟的静默错位。
//
// ── 为什么值得再试一次原生 ──
//
// Stack Overflow 上有人报告过同一个现象：IsControlElement 应该为 true
// 却返回 false，Reply 里说"这似乎是 UIAutomationCore.dll 内部的一个问题"，
// 并建议改用 UIAComWrapper —— 一个绕过托管 UIAutomationClient 的替代实现。
//
// 我这里要验证的是同一件事的两面：
//   ① 托管 AutomationElement.FromHandle 在 .NET 进程里读到 0
//   ② 原生 IUIAutomation.ElementFromHandle 在同一进程里读到多少
//
// 如果原生能读到，说明问题在托管封装，换成原生实现即可。
// 如果原生也读到 0，说明在更底层。

using System;
using System.Text;
using System.Runtime.InteropServices;

class RawUia2
{
    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    class CUIAutomation { }

    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void CompareElements(IntPtr el1, IntPtr el2, out int same);
        void CompareRuntimeIds(IntPtr r1, IntPtr r2, out int same);
        void GetRootElement(out IUIAutomationElement root);
        void ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        void ElementFromPoint(POINT pt, out IUIAutomationElement element);
        void GetFocusedElement(out IUIAutomationElement element);
        void GetRootElementBuildCache(IntPtr req, out IUIAutomationElement element);
        void ElementFromHandleBuildCache(IntPtr hwnd, IntPtr req, out IUIAutomationElement element);
        void ElementFromPointBuildCache(POINT pt, IntPtr req, out IUIAutomationElement element);
        // ★ 上一版漏了这个，导致后面每个方法的 vtable 位置全部错位一格，
        //   于是 CreateTrueCondition 落到别的槽上，返回 null。
        void GetFocusedElementBuildCache(IntPtr req, out IUIAutomationElement element);
        void CreateTreeWalker(IUIAutomationCondition cond, out IntPtr walker);
        void get_ControlViewWalker(out IntPtr walker);
        void get_ContentViewWalker(out IntPtr walker);
        void get_RawViewWalker(out IntPtr walker);
        void get_RawViewCondition(out IUIAutomationCondition cond);
        void get_ControlViewCondition(out IUIAutomationCondition cond);
        void get_ContentViewCondition(out IUIAutomationCondition cond);
        void CreateCacheRequest(out IntPtr req);
        void CreateTrueCondition(out IUIAutomationCondition cond);
        void CreateFalseCondition(out IUIAutomationCondition cond);
        void CreatePropertyCondition(int id, object value, out IUIAutomationCondition cond);
    }

    // 条件对象：真实接口有 get_Value 之类，这里只当不透明指针用，
    // 但**必须声明成接口**，否则封送层会当成 IntPtr 传值。
    [ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationCondition
    {
        void GetPropertyValue(int propertyId, out object value);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out IntPtr runtimeId);
        void FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement found);
        // ★ 关键：FindAll 的 out 参数是 IUIAutomationElementArray，不是 IntPtr
        void FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray found);
        void FindFirstBuildCache(int scope, IUIAutomationCondition c, IntPtr cache, out IUIAutomationElement found);
        void FindAllBuildCache(int scope, IUIAutomationCondition c, IntPtr cache, out IUIAutomationElementArray found);
        void BuildUpdatedCache(IntPtr cache, out IUIAutomationElement updated);
        void GetCurrentPropertyValue(int propertyId, out object value);
        void GetCurrentPropertyValueEx(int propertyId, int ignoreDefault, out object value);
        void GetCachedPropertyValue(int propertyId, out object value);
        void GetCachedPropertyValueEx(int propertyId, int ignoreDefault, out object value);
        void GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr pattern);
        void GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr pattern);
        void GetCurrentPattern(int patternId, out IntPtr pattern);
        void GetCachedPattern(int patternId, out IntPtr pattern);
        void GetCachedParent(out IUIAutomationElement parent);
        void GetCachedChildren(out IUIAutomationElementArray children);
        void get_CurrentProcessId(out int v);
        void get_CurrentControlType(out int v);
        void get_CurrentLocalizedControlType(out string v);
        void get_CurrentName(out string v);
    }

    [ComImport, Guid("14314595-B4BC-4055-95F2-58F2E42C9855"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElementArray
    {
        void get_Length(out int length);
        void GetElement(int index, out IUIAutomationElement element);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    const int TreeScope_Descendants = 4;

    [STAThread]
    static void Main(string[] args)
    {
        long target = args.Length > 0 ? long.Parse(args[0]) : 66988;

        var kids = new System.Collections.Generic.List<IntPtr>();
        var names = new System.Collections.Generic.List<string>();
        EnumChildWindows(new IntPtr(target), delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            kids.Add(h); names.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);

        var uia = (IUIAutomation)new CUIAutomation();
        IUIAutomationCondition trueCond;
        uia.CreateTrueCondition(out trueCond);
        Console.WriteLine("CreateTrueCondition: " + (trueCond != null));

        for (int i = 0; i < kids.Count; i++)
        {
            Console.WriteLine();
            Console.WriteLine("--- " + names[i] + " hwnd=" + kids[i].ToInt64() + " ---");
            IUIAutomationElement el;
            try { uia.ElementFromHandle(kids[i], out el); }
            catch (Exception e) { Console.WriteLine("  ElementFromHandle 异常: " + e.GetType().Name); continue; }
            if (el == null) { Console.WriteLine("  null"); continue; }

            try
            {
                string nm = "";
                int ct = -1;
                try { el.get_CurrentName(out nm); } catch { }
                try { el.get_CurrentControlType(out ct); } catch { }
                Console.Write("  name='" + nm + "' controlType=" + ct);

                IUIAutomationElementArray arr;
                el.FindAll(TreeScope_Descendants, trueCond, out arr);
                int len = 0;
                if (arr != null) arr.get_Length(out len);
                Console.WriteLine("  **Descendants = " + len + "**");

                // 前 5 个，看是不是真有内容
                for (int k = 0; k < Math.Min(len, 5); k++)
                {
                    IUIAutomationElement one;
                    arr.GetElement(k, out one);
                    if (one == null) continue;
                    string n2 = ""; int c2 = -1;
                    try { one.get_CurrentName(out n2); } catch { }
                    try { one.get_CurrentControlType(out c2); } catch { }
                    Console.WriteLine("    [" + k + "] ct=" + c2 + " '" + n2 + "'");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("  异常: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
