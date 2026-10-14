using System;
using System.Runtime.InteropServices;
using System.Text;

class ApiProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr h, string n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string n);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CompareObjectHandles(IntPtr a, IntPtr b);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== API 存在性（按模块导出表查）===");
        IntPtr k = GetModuleHandleW("kernel32.dll");
        IntPtr u = GetModuleHandleW("user32.dll");
        Console.WriteLine("kernel32 = " + k + "   user32 = " + u);

        string[] kern = { "CompareObjectHandles" };
        foreach (string n in kern)
        {
            IntPtr p = GetProcAddress(k, n);
            Console.WriteLine("  kernel32!" + n + " -> " + (p != IntPtr.Zero ? "存在 @" + p : "不存在"));
        }
        string[] user = { "OpenInputDesktop", "SetThreadDesktop", "GetThreadDesktop", "CloseDesktop", "SetWindowsHookExW" };
        foreach (string n in user)
        {
            IntPtr p = GetProcAddress(u, n);
            Console.WriteLine("  user32!" + n + " -> " + (p != IntPtr.Zero ? "存在 @" + p : "不存在"));
        }

        Console.WriteLine();
        Console.WriteLine("=== 实际调用测试（逐项，隔离崩溃点）===");

        IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
        try
        {
            a = OpenInputDesktop(0, false, 0x0001 | 0x0080 | 0x0100);
            Console.WriteLine("  OpenInputDesktop(0x181) = " + a +
                (a == IntPtr.Zero ? "  err=" + Marshal.GetLastWin32Error() : ""));
        }
        catch (Exception e) { Console.WriteLine("  OpenInputDesktop 抛异常: " + e.GetType().Name); }

        try
        {
            b = GetThreadDesktop(GetCurrentThreadId());
            Console.WriteLine("  GetThreadDesktop      = " + b);
        }
        catch (Exception e) { Console.WriteLine("  GetThreadDesktop 抛异常: " + e.GetType().Name); }

        if (a != IntPtr.Zero && b != IntPtr.Zero)
        {
            try
            {
                bool same = CompareObjectHandles(a, b);
                Console.WriteLine("  CompareObjectHandles  = " + same + "  → " +
                    (same ? "同一对象" : "不同对象"));
            }
            catch (Exception e)
            {
                Console.WriteLine("  ★ CompareObjectHandles 抛异常: " + e.GetType().FullName);
                Console.WriteLine("     message: " + e.Message);
                EntryPointNotFoundException ep = e as EntryPointNotFoundException;
                if (ep != null) Console.WriteLine("     → 这是 EntryPointNotFoundException：本机 kernel32 没有这个导出");
            }
        }
        else
        {
            Console.WriteLine("  （句柄为空，跳过 CompareObjectHandles）");
        }
    }
}
