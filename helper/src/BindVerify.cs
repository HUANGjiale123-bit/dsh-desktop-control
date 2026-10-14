/*
 * BindVerify —— 验证「启动最早处绑定输入桌面」能否让注入生效
 *
 * 已知事实：
 *   · GetThreadDesktop(tid)=308 与 OpenInputDesktop()=720 不是同一个句柄
 *   · 在该状态下 SendInput 返回成功（键盘 2 / 鼠标 1）但完全无效，光标不动
 *   · SetThreadDesktop 在本线程「已建窗口或装过钩子」后必然失败
 *
 * 因此本程序严格按顺序做，且**每一步之后都实测注入是否生效**：
 *   1. 立刻读桌面句柄（此时什么都还没做）
 *   2. SetThreadDesktop(输入桌面)
 *   3. 复读确认切换成功
 *   4. 再测 SendInput 相对位移
 *
 * 关键纪律：本程序在 Main 里不调用任何可能创建窗口的东西（不 SetProcessDPIAware、
 * 不碰 COM、不碰 UIA），确保 SetThreadDesktop 有机会成功。
 *
 * 用法：dsc-bindverify
 */

using System;
using System.Runtime.InteropServices;

class BindVerify
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll")]
    static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int i, System.Text.StringBuilder b, uint len, out uint need);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_WRITEOBJECTS = 0x0080, DESKTOP_SWITCHDESKTOP = 0x0100;

    static string Name(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        var sb = new System.Text.StringBuilder(512);
        uint n;
        return GetUserObjectInformationW(h, 2, sb, (uint)sb.Capacity * 2, out n) ? sb.ToString() : "(err)";
    }

    /// <summary>用 SendInput 相对位移 +25,+25，回读真实位移。返回是否移动。</summary>
    static bool TryMove(out string detail)
    {
        POINT b; GetCursorPos(out b);
        INPUT[] ms = new INPUT[1];
        ms[0].type = 0;
        ms[0].mi.dx = 25; ms[0].mi.dy = 25;
        ms[0].mi.dwFlags = 0x0001;
        uint n = SendInput(1, ms, Marshal.SizeOf(typeof(INPUT)));
        int err = Marshal.GetLastWin32Error();
        System.Threading.Thread.Sleep(200);
        POINT a; GetCursorPos(out a);
        int dx = a.X - b.X, dy = a.Y - b.Y;
        detail = "SendInput 返回 " + n + "  err=" + err + "  位移=(" + dx + "," + dy + ")";
        return dx != 0 || dy != 0;
    }

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 绑定输入桌面 → 注入是否生效 ===");

        uint tid = GetCurrentThreadId();
        IntPtr ws = GetProcessWindowStation();
        IntPtr deskBefore = GetThreadDesktop(tid);

        Console.WriteLine("\n[1] 初始状态（尚未做任何事）");
        Console.WriteLine("    窗口站     = " + ws + "  name=" + Name(ws));
        Console.WriteLine("    线程桌面   = " + deskBefore + "  name=" + Name(deskBefore));

        IntPtr input = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP);
        Console.WriteLine("    输入桌面   = " + input + "  name=" + Name(input));
        Console.WriteLine("    句柄相同?  = " + (deskBefore == input));

        string d0;
        bool m0 = TryMove(out d0);
        Console.WriteLine("\n[2] 绑定前的注入测试");
        Console.WriteLine("    " + d0 + "   → " + (m0 ? "✅ 生效" : "❌ 无效"));

        if (input == IntPtr.Zero)
        {
            Console.WriteLine("\nOpenInputDesktop 失败，无法继续。");
            return 2;
        }

        Console.WriteLine("\n[3] SetThreadDesktop(输入桌面)");
        bool ok = SetThreadDesktop(input);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        Console.WriteLine("    返回 = " + ok + (ok ? "" : "  err=" + err + " (" +
            new System.ComponentModel.Win32Exception(err).Message + ")"));

        IntPtr deskAfter = GetThreadDesktop(tid);
        Console.WriteLine("    复读线程桌面 = " + deskAfter + "  与输入桌面相同? = " + (deskAfter == input));

        string d1;
        bool m1 = TryMove(out d1);
        Console.WriteLine("\n[4] 绑定后的注入测试");
        Console.WriteLine("    " + d1 + "   → " + (m1 ? "✅ 生效" : "❌ 无效"));

        Console.WriteLine("\n=== 判读 ===");
        if (!m0 && m1) Console.WriteLine("  ✅ 绑定输入桌面解决了注入问题 —— 这就是根因。");
        else if (!m0 && !m1) Console.WriteLine("  ❌ 绑定无效 —— 桌面不是原因，需另找。");
        else if (m0 && m1) Console.WriteLine("  ⚠️ 绑定前就能注入 —— 说明桌面会变化，问题可能是间歇性的。");
        else Console.WriteLine("  ⚠️ 绑定前能注入、绑定后不能 —— 说明绑定反而有害，应去掉。");
        return 0;
    }
}
