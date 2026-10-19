// 原生 IUIAutomation 客户端对照 —— Codex 明确要求的那一个。
//
// ── 之前错在哪 ──
//
// 我用 UiaHostProviderFromHwnd 当"读入口"，那是**服务端**实现
// get_HostRawElementProvider 用的 API，拿到的对象没有 Fragment 接口
// 不能证明应用没有内容 provider。外部复核已经指出这一点。
//
// 正确的客户端对照是：
//   CUIAutomation → IUIAutomation.ElementFromHandle(hwnd)
//   → 读 CurrentProviderDescription / Name / ControlType
//   → TreeWalker / FindAll 遍历有界子树
//
// 这个程序做这些，并且**不依赖托管 AutomationElement**。

using System;
using System.Text;
using System.Runtime.InteropServices;

class RawUia
{
    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    class CUIAutomation { }

    // IUIAutomation 的方法顺序必须严格照 IDL，错一个就 AccessViolation。
    // 只声明到 ElementFromHandle 之前需要的那些。
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
        void CreateCacheRequest(out IntPtr req);
        void CreateTrueCondition(out IntPtr cond);
        void CreateFalseCondition(out IntPtr cond);
        void CreatePropertyCondition(int id, object value, out IntPtr cond);
        void CreateTreeWalker(IntPtr pWalkerCondition, out IntPtr pWalker);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    // IUIAutomationElement 只有它有 get_CurrentProviderDescription 等，
    // 这里只取几个属性，方法顺序同样严格。
    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out IntPtr runtimeId);
        void FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
        void FindAll(int scope, IntPtr condition, out IntPtr found);
        void FindFirstBuildCache(int scope, IntPtr condition, IntPtr cache, out IUIAutomationElement found);
        void FindAllBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr found);
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
        void GetCachedChildren(out IntPtr children);
        void get_CurrentProcessId(out int v);
        void get_CurrentControlType(out int v);
        void get_CurrentLocalizedControlType(out string v);
        void get_CurrentName(out string v);
        void get_CurrentAcceleratorKey(out string v);
        void get_CurrentAccessKey(out string v);
        void get_CurrentHasKeyboardFocus(out int v);
        void get_CurrentIsKeyboardFocusable(out int v);
        void get_CurrentIsEnabled(out int v);
        void get_CurrentAutomationId(out string v);
        void get_CurrentClassName(out string v);
        void get_CurrentHelpText(out string v);
        void get_CurrentCulture(out int v);
        void get_CurrentIsControlElement(out int v);
        void get_CurrentIsContentElement(out int v);
        void get_CurrentIsPassword(out int v);
        void get_CurrentNativeWindowHandle(out IntPtr v);
        void get_CurrentItemType(out string v);
        void get_CurrentIsOffscreen(out int v);
        void get_CurrentOrientation(out int v);
        void get_CurrentFrameworkId(out string v);
        void get_CurrentIsRequiredForForm(out int v);
        void get_CurrentItemStatus(out string v);
        void get_CurrentBoundingRectangle(out UiaRect v);
        void get_CurrentLabeledBy(out IUIAutomationElement v);
        void get_CurrentAriaRole(out string v);
        void get_CurrentAriaProperties(out string v);
        void get_CurrentIsDataValidForForm(out int v);
        void get_CurrentControllerFor(out IntPtr v);
        void get_CurrentDescribedBy(out IntPtr v);
        void get_CurrentFlowsTo(out IntPtr v);
        void get_CurrentProviderDescription(out string v);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UiaRect { public double left, top, width, height; }

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    const int TreeScope_Descendants = 4;
    const int TreeScope_Children = 2;

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

        Console.WriteLine("=== 原生 IUIAutomation.ElementFromHandle ===");
        IUIAutomation uia = null;
        try
        {
            uia = (IUIAutomation)new CUIAutomation();
            Console.WriteLine("CUIAutomation 创建成功");
        }
        catch (Exception e)
        {
            Console.WriteLine("CUIAutomation 创建失败: " + e.Message);
            return;
        }

        for (int i = 0; i < kids.Count; i++)
        {
            Console.WriteLine("");
            Console.WriteLine("--- " + names[i] + " hwnd=" + kids[i].ToInt64() + " ---");
            IUIAutomationElement el = null;
            try
            {
                uia.ElementFromHandle(kids[i], out el);
            }
            catch (Exception e)
            {
                Console.WriteLine("  ElementFromHandle 抛异常: " + e.GetType().Name + ": " + e.Message);
                continue;
            }
            if (el == null) { Console.WriteLine("  返回 null"); continue; }

            try
            {
                string nm = "", cls = "", fw = "", prov = "";
                int ct = -1, pid = -1;
                try { el.get_CurrentName(out nm); } catch { }
                try { el.get_CurrentClassName(out cls); } catch { }
                try { el.get_CurrentFrameworkId(out fw); } catch { }
                try { el.get_CurrentProviderDescription(out prov); } catch { }
                try { el.get_CurrentControlType(out ct); } catch { }
                try { el.get_CurrentProcessId(out pid); } catch { }
                Console.WriteLine("  pid=" + pid + " class=" + cls + " framework=" + fw);
                Console.WriteLine("  name='" + nm + "' controlType=" + ct);
                Console.WriteLine("  **ProviderDescription** = " + prov);

                // 用原生条件对象做 FindAll
                IntPtr cond;
                uia.CreateTrueCondition(out cond);
                IntPtr found;
                el.FindAll(TreeScope_Descendants, cond, out found);
                Console.WriteLine("  FindAll(Descendants) 返回指针 = " + found);
            }
            catch (Exception e)
            {
                Console.WriteLine("  属性读取异常: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
