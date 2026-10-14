using System;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

class ClickDiag
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

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
    const uint MOUSEEVENTF_MOVE = 0x0001;

    static string T(IntPtr h)
    {
        var sb = new StringBuilder(120);
        GetWindowTextW(h, sb, 120);
        return sb.ToString();
    }

    static void Line(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(@"D:\dsh workspace\dsh-desktop-control\.state\clickdiag.log",
            DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); } catch { }
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        SetProcessDPIAware();
        System.Threading.Thread.Sleep(100);

        int tx = args.Length > 0 ? int.Parse(args[0]) : 1800;
        int ty = args.Length > 1 ? int.Parse(args[1]) : 700;

        IntPtr fg = GetForegroundWindow();
        Line("=== click 诊断 ===");
        Line("目标坐标 = (" + tx + "," + ty + ")");
        Line("前台窗口 = " + fg + " [" + T(fg) + "]");

        POINT c;
        GetCursorPos(out c);
        Line("当前光标 = (" + c.X + "," + c.Y + ")");
        IntPtr at = WindowFromPoint(c);
        Line("光标处窗口 = " + at + " [" + T(GetAncestor(at, 2)) + "]");

        POINT tp = new POINT(); tp.X = tx; tp.Y = ty;
        IntPtr target = WindowFromPoint(tp);
        Line("目标处窗口 = " + target + " [" + T(GetAncestor(target, 2)) + "]");

        Line("");
        Line("--- 逐步尝试移动光标 ---");

        // 1) 当前点（应当必然成功或必然失败，是基准）
        bool m0 = SetCursorPos(c.X, c.Y);
        Line("[1] SetCursorPos(当前点 " + c.X + "," + c.Y + ") = " + m0);

        // 2) 小步移开 10 像素
        bool m1 = SetCursorPos(c.X + 10, c.Y + 10);
        System.Threading.Thread.Sleep(120);
        POINT a1; GetCursorPos(out a1);
        Line("[2] SetCursorPos(当前点+10,+10) = " + m1 + "  实际=(" + a1.X + "," + a1.Y + ")");

        // 3) 目标点
        bool m2 = SetCursorPos(tx, ty);
        System.Threading.Thread.Sleep(120);
        POINT a2; GetCursorPos(out a2);
        Line("[3] SetCursorPos(目标点) = " + m2 + "  实际=(" + a2.X + "," + a2.Y + ")");

        // 4) SendInput 相对
        POINT b3; GetCursorPos(out b3);
        INPUT[] mv = new INPUT[1];
        mv[0].type = 0; mv[0].mi.dx = 20; mv[0].mi.dy = 20; mv[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        uint n4 = SendInput(1, mv, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(150);
        POINT a3; GetCursorPos(out a3);
        Line("[4] SendInput 相对(+20,+20) 返回=" + n4 + "  位移=(" + (a3.X - b3.X) + "," + (a3.Y - b3.Y) + ")");

        Line("");
        bool any = m0 || m1 || m2 || (a3.X != b3.X);
        Line(any ? "结论：注入部分可用" : "结论：此刻注入完全不可用");
        return 0;
    }
}
