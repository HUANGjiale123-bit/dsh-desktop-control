using System;
using System.Runtime.InteropServices;
using System.Threading;

class StepMoveTest
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

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
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        SetProcessDPIAware();
        Thread.Sleep(100);

        int tx = args.Length > 0 ? int.Parse(args[0]) : 1500;
        int ty = args.Length > 1 ? int.Parse(args[1]) : 200;

        POINT start;
        GetCursorPos(out start);
        Console.WriteLine("起始光标 = (" + start.X + "," + start.Y + ")   目标 = (" + tx + "," + ty + ")");
        Console.WriteLine();

        // 方法1：一步跳过去
        Console.WriteLine("[1] 一步跳转 SetCursorPos(" + tx + "," + ty + ")");
        bool ok1 = SetCursorPos(tx, ty);
        Thread.Sleep(250);
        POINT a1; GetCursorPos(out a1);
        Console.WriteLine("    返回=" + ok1 + "  实际=(" + a1.X + "," + a1.Y + ")" +
                          (a1.X == tx && a1.Y == ty ? "  ✅" : "  ❌"));
        Console.WriteLine();

        // 回到起点
        SetCursorPos(start.X, start.Y);
        Thread.Sleep(200);
        POINT b2; GetCursorPos(out b2);
        Console.WriteLine("[2] 分 20 步走过去（每步 ~" +
                          (int)(Math.Sqrt(Math.Pow(tx - b2.X, 2) + Math.Pow(ty - b2.Y, 2)) / 20) + " 像素）");
        int steps = 20;
        int okSteps = 0;
        for (int i = 1; i <= steps; i++)
        {
            int x = b2.X + (tx - b2.X) * i / steps;
            int y = b2.Y + (ty - b2.Y) * i / steps;
            bool ok = SetCursorPos(x, y);
            Thread.Sleep(60);
            POINT cur; GetCursorPos(out cur);
            bool hit = Math.Abs(cur.X - x) <= 2 && Math.Abs(cur.Y - y) <= 2;
            if (hit) okSteps++;
        }
        Thread.Sleep(200);
        POINT a2; GetCursorPos(out a2);
        Console.WriteLine("    " + okSteps + "/" + steps + " 步成功   最终=(" + a2.X + "," + a2.Y + ")" +
                          (Math.Abs(a2.X - tx) <= 3 && Math.Abs(a2.Y - ty) <= 3 ? "  ✅ 到达" : "  ❌ 未到达"));
        Console.WriteLine();

        // 方法3：SendInput 相对小步
        Console.WriteLine("[3] SendInput 相对模式，分 20 次小步移动");
        int moved = 0;
        for (int i = 0; i < 20; i++)
        {
            POINT p1; GetCursorPos(out p1);
            int needX = tx - p1.X, needY = ty - p1.Y;
            if (Math.Abs(needX) <= 2 && Math.Abs(needY) <= 2) break;
            INPUT[] m = new INPUT[1];
            m[0].type = 0;
            m[0].mi.dx = Math.Sign(needX) * Math.Min(20, Math.Abs(needX));
            m[0].mi.dy = Math.Sign(needY) * Math.Min(20, Math.Abs(needY));
            m[0].mi.dwFlags = MOUSEEVENTF_MOVE;
            SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(60);
            POINT p2; GetCursorPos(out p2);
            if (p2.X != p1.X || p2.Y != p1.Y) moved++;
        }
        POINT a3; GetCursorPos(out a3);
        Console.WriteLine("    有效步数 = " + moved + "/20   最终=(" + a3.X + "," + a3.Y + ")" +
                          (Math.Abs(a3.X - tx) <= 3 && Math.Abs(a3.Y - ty) <= 3 ? "  ✅ 到达" : "  ❌ 未到达"));

        Console.WriteLine();
        Console.WriteLine("=== 判读 ===");
        Console.WriteLine("  [1] 失败但 [2]/[3] 成功 → 大跳转被拦，分步可以走");
        Console.WriteLine("  全部失败               → 与步长无关");
        return 0;
    }
}
