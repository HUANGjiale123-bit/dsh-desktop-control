// 原生 IRawElementProvider 遍历实验。
//
// ── 为什么 ──
//
// UiaHostProviderFromHwnd 对 SuYou 的每个子窗口都返回 hr=0（成功），
// 说明 provider 是存在的；但托管的 AutomationElement.FromHandle 只读到 4 个节点。
// 所以要绕开托管封装，直接用 provider 的 COM 接口走一遍。
//
// ── 接口顺序必须严格照 IDL，错一个方法就 AccessViolation ──
//
//   IRawElementProviderSimple (d6dd68d1-86fd-4332-8666-9abedea2d24c)
//     get_ProviderOptions
//     GetPatternProvider
//     GetPropertyValue
//     get_HostRawElementProvider
//
//   IRawElementProviderFragment (f7063da8-8359-439c-9297-bbc5299a7d87)
//     Navigate
//     GetRuntimeId
//     get_BoundingRectangle
//     GetEmbeddedFragmentRoots
//     SetFocus
//     get_FragmentRoot
//
// ── 上一版错在哪 ──
//
// UiaReturnRawElementProvider 是**服务端**回调，不是客户端读接口。
// 我拿它当"读"用，参数类型也不对，于是 AccessViolation。
// 这一版只用 UiaHostProviderFromHwnd + 两个 provider 接口。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

class NativeWalk
{
    const int NavigateDirection_Parent = 0;
    const int NavigateDirection_NextSibling = 1;
    const int NavigateDirection_PreviousSibling = 2;
    const int NavigateDirection_FirstChild = 3;
    const int NavigateDirection_LastChild = 4;

    const int UIA_NamePropertyId = 30005;
    const int UIA_ControlTypePropertyId = 30003;
    const int UIA_BoundingRectanglePropertyId = 30001;
    const int UIA_IsContentElementPropertyId = 30017;

    [StructLayout(LayoutKind.Sequential)]
    struct UiaRect { public double left, top, width, height; }

    [ComImport, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRawElementProviderSimple
    {
        int get_ProviderOptions(out int pRetVal);
        int GetPatternProvider(int patternId, out IntPtr pRetVal);
        int GetPropertyValue(int propertyId, [In, Out] ref object pRetVal);
        int get_HostRawElementProvider(out IntPtr pRetVal);
    }

    [ComImport, Guid("f7063da8-8359-439c-9297-bbc5299a7d87"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRawElementProviderFragment
    {
        int Navigate(int direction, out IntPtr pRetVal);
        int GetRuntimeId(out IntPtr pRetVal);
        int get_BoundingRectangle(out UiaRect pRetVal);
        int GetEmbeddedFragmentRoots(out IntPtr pRetVal);
        int SetFocus();
        int get_FragmentRoot(out IntPtr pRetVal);
    }

    [DllImport("UIAutomationCore.dll")]
    static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IntPtr provider);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    static string NameOf(IRawElementProviderSimple p)
    {
        try
        {
            object v = null;
            int hr = p.GetPropertyValue(UIA_NamePropertyId, ref v);
            if (hr == 0 && v != null) return v.ToString();
        }
        catch { }
        return "";
    }

    static int TypeOf(IRawElementProviderSimple p)
    {
        try
        {
            object v = null;
            int hr = p.GetPropertyValue(UIA_ControlTypePropertyId, ref v);
            if (hr == 0 && v != null) return Convert.ToInt32(v);
        }
        catch { }
        return -1;
    }

    [STAThread]
    static void Main(string[] args)
    {
        long target = args.Length > 0 ? long.Parse(args[0]) : 66988;

        var kids = new List<IntPtr>();
        var names = new List<string>();
        EnumChildWindows(new IntPtr(target), delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            kids.Add(h); names.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);

        for (int i = 0; i < kids.Count; i++)
        {
            if (names[i].IndexOf("RenderWidgetHost") < 0 &&
                names[i].IndexOf("WidgetWin_1") < 0) continue;

            Console.WriteLine("=== " + names[i] + " hwnd=" + kids[i].ToInt64() + " ===");
            IntPtr prov;
            int hr = UiaHostProviderFromHwnd(kids[i], out prov);
            if (hr != 0 || prov == IntPtr.Zero)
            {
                Console.WriteLine("  provider 取不到 hr=0x" + hr.ToString("X8"));
                continue;
            }

            try
            {
                var simple = (IRawElementProviderSimple)Marshal.GetObjectForIUnknown(prov);
                Console.WriteLine("  根 Name='" + NameOf(simple) + "' ControlType=" + TypeOf(simple));

                // QueryInterface 到 Fragment
                var frag = simple as IRawElementProviderFragment;
                if (frag == null)
                {
                    Console.WriteLine("  ** 不支持 IRawElementProviderFragment **");
                }
                else
                {
                    IntPtr childPtr;
                    int hrc = frag.Navigate(NavigateDirection_FirstChild, out childPtr);
                    Console.WriteLine("  Navigate(FirstChild) hr=0x" + hrc.ToString("X8") +
                        " ptr=" + childPtr);
                    if (hrc == 0 && childPtr != IntPtr.Zero)
                    {
                        var child = (IRawElementProviderSimple)Marshal.GetObjectForIUnknown(childPtr);
                        Console.WriteLine("    第一个孩子 Name='" + NameOf(child) +
                            "' ControlType=" + TypeOf(child));
                        Marshal.Release(childPtr);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("  异常 " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Marshal.Release(prov);
            }
        }
    }
}
