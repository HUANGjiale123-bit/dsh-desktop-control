using System;
using System.Runtime.InteropServices;

class LogicalCoordTest
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
    static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

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
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    static string Cur() { POINT p; GetCursorPos(out p); return p.X + "," + p.Y; }

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 先看 DPI awareness 到底生效没有
        Console.WriteLine("=== DPI / 坐标系 ===");
        Console.WriteLine("调用前 SM_CXSCREEN = " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        bool aware = SetProcessDPIAware();
        System.Threading.Thread.Sleep(50);
        Console.WriteLine("SetProcessDPIAware() = " + aware);
        Console.WriteLine("调用后 SM_CXSCREEN = " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        Console.WriteLine("光标 = " + Cur() + "   前台 = " + GetForegroundWindow());
        Console.WriteLine();

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);

        // 参数：逻辑坐标
        int[] targets = args.Length >= 2
            ? new int[] { int.Parse(args[0]), int.Parse(args[1]) }
            : new int[] { 489, 39 };

        Console.WriteLine("=== A: SetCursorPos 逻辑坐标 (" + targets[0] + "," + targets[1] + ") ===");
        bool ok = SetCursorPos(targets[0], targets[1]);
        System.Threading.Thread.Sleep(200);
        Console.WriteLine("  返回 = " + ok + "  实际 = " + Cur());
        POINT pp; GetCursorPos(out pp);
        Console.WriteLine("  该点窗口 = " + WindowFromPoint(pp));
        Console.WriteLine();

        Console.WriteLine("=== B: SendInput 绝对模式 → 逻辑坐标 ===");
        for (int i = 0; i < targets.Length; i += 2)
        {
            int lx = targets[i], ly = targets[i + 1];
            int nx = (int)Math.Round(lx * 65535.0 / (sw > 1 ? sw - 1 : 1));
            int ny = (int)Math.Round(ly * 65535.0 / (sh > 1 ? sh - 1 : 1));
            POINT b; GetCursorPos(out b);
            INPUT[] m = new INPUT[1];
            m[0].type = 0; m[0].mi.dx = nx; m[0].mi.dy = ny;
            m[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
            uint n = SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
            System.Threading.Thread.Sleep(200);
            POINT a; GetCursorPos(out a);
            Console.WriteLine("  逻辑(" + lx + "," + ly + ") 归一化(" + nx + "," + ny + ") 返回=" + n +
                              "  实际=(" + a.X + "," + a.Y + ")  位移=(" + (a.X - b.X) + "," + (a.Y - b.Y) + ")");
        }
        Console.WriteLine();

        Console.WriteLine("=== C: 相对移动 ===");
        POINT b2; GetCursorPos(out b2);
        INPUT[] r = new INPUT[1];
        r[0].type = 0; r[0].mi.dx = 30; r[0].mi.dy = 30; r[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        uint rn = SendInput(1, r, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(200);
        POINT a2; GetCursorPos(out a2);
        Console.WriteLine("  相对(+30,+30) 返回=" + rn + "  位移=(" + (a2.X - b2.X) + "," + (a2.Y - b2.Y) + ")");
        return 0;
    }
}
