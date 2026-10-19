// 原生 IUIAutomation 对照实验。
//
// ── 为什么做这个 ──
//
// 托管的 AutomationElement.FromHandle 对同一窗口只能读到外壳，
// 而 PowerShell 里同样的托管调用能读到完整树。九个假设（位数、入口点、
// UIA 预热、枚举、句柄、STA、WinForms、OLE、CoInitializeSecurity）都已排除。
//
// Codex 早就建议过"做一次原生对照"——现在是最合适的时机：
// 如果原生 IUIAutomation 能读到，说明差异在托管封装；
// 如果也读不到，说明差异在进程，托管/原生都一样。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class NativeUia
{
    // ── CUIAutomation coclass ──
    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    private class CUIAutomation { }

    // ── 只用到的两个接口，方法顺序必须和 IDL 一致 ──
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        // 前三个方法的签名必须占位正确，否则 vtable 偏移错位
        void CompareElements([MarshalAs(UnmanagedType.Interface)] object el1,
                             [MarshalAs(UnmanagedType.Interface)] object el2,
                             out int areSame);
        void CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
        void GetRootElement([MarshalAs(UnmanagedType.Interface)] out object root);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId([MarshalAs(UnmanagedType.SafeArray)] out object[] runtimeId);
        void FindFirst(int scope, [MarshalAs(UnmanagedType.Interface)] object condition,
                       [MarshalAs(UnmanagedType.Interface)] out object found);
    }

    [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
    private static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IntPtr provider);

    [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
    private static extern int UiaReturnRawElementProvider(IntPtr hwnd, IntPtr wParam,
        IntPtr lParam, out IntPtr element);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [STAThread]
    static void Main(string[] args)
    {
        long target = args.Length > 0 ? long.Parse(args[0]) : 66988;

        var kids = new List<IntPtr>();
        var names = new List<string>();
        EnumChildWindows(new IntPtr(target), delegate(IntPtr h, IntPtr l)
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            kids.Add(h); names.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);

        Console.WriteLine("=== UiaHostProviderFromHwnd（每个子窗口）===");
        for (int i = 0; i < kids.Count; i++)
        {
            IntPtr prov;
            int hr = UiaHostProviderFromHwnd(kids[i], out prov);
            Console.WriteLine("  " + names[i].PadRight(30) +
                " hr=0x" + hr.ToString("X8") + " ptr=" + prov);
            if (prov != IntPtr.Zero) Marshal.Release(prov);
        }

        Console.WriteLine("");
        Console.WriteLine("=== UiaReturnRawElementProvider（每个子窗口）===");
        for (int i = 0; i < kids.Count; i++)
        {
            IntPtr el;
            int hr = UiaReturnRawElementProvider(kids[i], IntPtr.Zero, IntPtr.Zero, out el);
            Console.WriteLine("  " + names[i].PadRight(30) +
                " hr=0x" + hr.ToString("X8") + " ptr=" + el);
            if (el != IntPtr.Zero) Marshal.Release(el);
        }

        Console.WriteLine("");
        Console.WriteLine("=== 原生 IUIAutomation 能否实例化 ===");
        try
        {
            var uia = (IUIAutomation)new CUIAutomation();
            Console.WriteLine("  CUIAutomation 创建: ok");
            object root;
            uia.GetRootElement(out root);
            Console.WriteLine("  GetRootElement: " + (root != null ? "ok" : "null"));
        }
        catch (Exception e)
        {
            Console.WriteLine("  失败: " + e.GetType().Name + ": " + e.Message);
        }
    }
}
