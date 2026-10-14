/*
 * DesktopBindTest —— 验证「绑定输入桌面」这个修复是否真的有效
 *
 * 背景：helper 里加了 BindInputDesktop()，但 click 仍然报 SetCursorPos 失败(2)。
 * 需要确认：是绑定本身失败，还是绑定之后桌面又变了，还是顺序问题。
 *
 * 按与 helper 完全相同的顺序执行，每步都打印结果：
 *   1. SetProcessDPIAware()
 *   2. 读当前线程桌面 + 打开输入桌面，比较句柄
 *   3. SetThreadDesktop(输入桌面)
 *   4. 复读，确认切换生效
 *   5. SetCursorPos 到目标点，报告结果
 *   6. 再复读，看桌面是否又变了
 *
 * 用法：dsc-desktopbind [x] [y]
 */

using System;
using System.Runtime.InteropServices;
using System.Text;

class DesktopBindTest
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int i, StringBuilder b, uint len, out uint need);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_WRITEOBJECTS = 0x0080, DESKTOP_SWITCHDESKTOP = 0x0100;

    static string Name(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        StringBuilder sb = new StringBuilder(512);
        uint need;
        return GetUserObjectInformationW(h, 2, sb, (uint)sb.Capacity * 2, out need) ? sb.ToString() : "(err)";
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int tx = args.Length > 0 ? int.Parse(args[0]) : 733;
        int ty = args.Length > 1 ? int.Parse(args[1]) : 59;

        Console.WriteLine("=== 输入桌面绑定测试 ===");
        Console.WriteLine("pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                          " tid=" + GetCurrentThreadId());

        Console.WriteLine("\n[1] SetProcessDPIAware() = " + SetProcessDPIAware());

        uint tid = GetCurrentThreadId();
        IntPtr threadDeskBefore = GetThreadDesktop(tid);
        IntPtr inputDesk = OpenInputDesktop(0, false,
            DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP);

        Console.WriteLine("\n[2] 绑定前状态");
        Console.WriteLine("    线程桌面   = " + threadDeskBefore + "  name=" + Name(threadDeskBefore));
        Console.WriteLine("    输入桌面   = " + inputDesk + "  name=" + Name(inputDesk));
        Console.WriteLine("    句柄相同?  = " + (threadDeskBefore == inputDesk));

        if (inputDesk == IntPtr.Zero)
        {
            Console.WriteLine("    OpenInputDesktop 失败 err=" + Marshal.GetLastWin32Error() + " —— 无法继续");
            return 2;
        }

        Console.WriteLine("\n[3] SetThreadDesktop(输入桌面)");
        bool ok = SetThreadDesktop(inputDesk);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        Console.WriteLine("    返回 = " + ok + (ok ? "" : "  err=" + err + " (" +
            new System.ComponentModel.Win32Exception(err).Message + ")"));

        Console.WriteLine("\n[4] 绑定后复读");
        IntPtr threadDeskAfter = GetThreadDesktop(tid);
        Console.WriteLine("    线程桌面   = " + threadDeskAfter + "  name=" + Name(threadDeskAfter));
        Console.WriteLine("    句柄相同?  = " + (threadDeskAfter == inputDesk) +
                          "   （与输入桌面句柄相等才算真的切过去了）");

        Console.WriteLine("\n[5] SetCursorPos(" + tx + "," + ty + ")");
        POINT b; GetCursorPos(out b);
        Console.WriteLine("    调用前光标 = " + b.X + "," + b.Y);
        bool moved = SetCursorPos(tx, ty);
        int cerr = moved ? 0 : Marshal.GetLastWin32Error();
        POINT a; GetCursorPos(out a);
        Console.WriteLine("    返回 = " + moved + (moved ? "" : "  err=" + cerr + " (" +
            new System.ComponentModel.Win32Exception(cerr).Message + ")"));
        Console.WriteLine("    调用后光标 = " + a.X + "," + a.Y +
                          (a.X == tx && a.Y == ty ? "  ✅ 精确命中" : "  ❌ 未命中"));

        Console.WriteLine("\n[6] 结束时再复读桌面（看是否又变了）");
        IntPtr threadDeskEnd = GetThreadDesktop(tid);
        Console.WriteLine("    线程桌面   = " + threadDeskEnd + "  与输入桌面相同? = " +
                          (threadDeskEnd == inputDesk));
        Console.WriteLine("    输入桌面句柄仍是 " + inputDesk + " 吗? 当前打开的是 " +
                          OpenInputDesktop(0, false, DESKTOP_READOBJECTS));

        if (ok && moved) Console.WriteLine("\n结论：绑定有效，坐标注入可用。");
        else if (!ok) Console.WriteLine("\n结论：SetThreadDesktop 失败 —— 绑定这一步就是问题所在。");
        else Console.WriteLine("\n结论：绑定成功了但 SetCursorPos 仍失败 —— 问题不在桌面绑定。");
        return 0;
    }
}
