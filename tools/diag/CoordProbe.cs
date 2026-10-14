using System;
using System.Runtime.InteropServices;

class CoordProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);

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

    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000;

    static string Cur() { POINT p; GetCursorPos(out p); return p.X + "," + p.Y; }

    static void Try(string label, int x, int y)
    {
        POINT b; GetCursorPos(out b);
        INPUT[] m = new INPUT[1];
        m[0].type = 0;
        m[0].mi.dx = x; m[0].mi.dy = y;
        m[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        uint n = SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(180);
        POINT a; GetCursorPos(out a);
        Console.WriteLine("  " + label.PadRight(26) + " 归一化(" + x + "," + y + ") 返回=" + n +
                          "  位移=(" + (a.X - b.X) + "," + (a.Y - b.Y) + ")");
    }

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("=== 坐标定位探测 ===");
        Console.WriteLine("SM_CXSCREEN=" + GetSystemMetrics(0) + " SM_CYSCREEN=" + GetSystemMetrics(1));
        Console.WriteLine("SM_CXVIRTUALSCREEN=" + GetSystemMetrics(78) + " SM_CYVIRTUALSCREEN=" + GetSystemMetrics(79));
        Console.WriteLine("光标 = " + Cur());
        Console.WriteLine("前台窗口 = " + GetForegroundWindow());

        // 光标当前所在点属于哪个窗口
        POINT c; GetCursorPos(out c);
        Console.WriteLine("光标处窗口 = " + WindowFromPoint(c));
        Console.WriteLine();

        Console.WriteLine("--- A: SetCursorPos 试几个点 ---");
        int[,] pts = { { 100, 100 }, { 700, 400 }, { 1200, 800 }, { 2200, 1300 } };
        for (int i = 0; i < 4; i++)
        {
            int x = pts[i, 0], y = pts[i, 1];
            bool ok = SetCursorPos(x, y);
            int err = Marshal.GetLastWin32Error();
            System.Threading.Thread.Sleep(120);
            Console.WriteLine("  SetCursorPos(" + x + "," + y + ") = " + ok +
                              "  实际=" + Cur() + "  (lastErr=" + err + ")");
        }

        Console.WriteLine();
        Console.WriteLine("--- B: SendInput 绝对模式，按 SM_CXSCREEN 归一化 ---");
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        if (sw <= 1) sw = 1;
        if (sh <= 1) sh = 1;
        for (int i = 0; i < 4; i++)
        {
            int x = pts[i, 0], y = pts[i, 1];
            int nx = (int)Math.Round(x * 65535.0 / (sw - 1));
            int ny = (int)Math.Round(y * 65535.0 / (sh - 1));
            Try("目标(" + x + "," + y + ")", nx, ny);
        }

        Console.WriteLine();
        Console.WriteLine("--- C: 相对模式多次 ---");
        for (int k = 1; k <= 3; k++)
        {
            POINT b; GetCursorPos(out b);
            INPUT[] m = new INPUT[1];
            m[0].type = 0; m[0].mi.dx = 20 * k; m[0].mi.dy = 20 * k; m[0].mi.dwFlags = MOUSEEVENTF_MOVE;
            uint n = SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
            System.Threading.Thread.Sleep(150);
            POINT a; GetCursorPos(out a);
            Console.WriteLine("  相对(+" + (20 * k) + ",+" + (20 * k) + ") 返回=" + n +
                              "  位移=(" + (a.X - b.X) + "," + (a.Y - b.Y) + ")");
        }
        return 0;
    }
}
