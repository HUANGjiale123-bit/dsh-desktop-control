using System;
using System.Runtime.InteropServices;
using System.Threading;

// 注入能力与"进程存活时间"的关系。
//
// 背景：Codex 的 computer use helper 是**常驻进程**，注入成功；
// 本项目的 helper 是**一次性进程**（每次调用起一个，做完就退），注入失败。
// 目前的对照已经把"启动方"这个变量测掉了（插件直接 spawn 无 shell 中间层，仍失败），
// 所以"进程存活时长"是下一个该测的变量。
//
// 做法：注入尝试重复多轮，每轮间隔 2 秒，看是否随时间推移变成可用。
// 判据始终是光标实际位置，不是返回值。
//
// 用法：dsc-injtiming [轮数]   默认 6 轮（约 12 秒）
class InjTiming
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
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }
    const uint MOVE = 0x0001;

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        SetProcessDPIAware();
        int rounds = args.Length > 0 ? int.Parse(args[0]) : 6;

        Console.WriteLine("=== 注入能力 vs 进程存活时间 ===");
        Console.WriteLine("pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        POINT s;
        GetCursorPos(out s);
        Console.WriteLine("起始光标 (" + s.X + "," + s.Y + ")");
        Console.WriteLine();

        long t0 = Environment.TickCount;
        int everWorked = 0;

        for (int r = 1; r <= rounds; r++)
        {
            // 交替目标，避免"已经在该点"造成的假成功
            int tx = (r % 2 == 1) ? 640 : 1440;
            int ty = (r % 2 == 1) ? 360 : 840;

            bool ret = SetCursorPos(tx, ty);
            Thread.Sleep(150);
            POINT a;
            GetCursorPos(out a);
            bool hit = (a.X == tx && a.Y == ty);
            if (hit) everWorked++;

            // SendInput 相对
            POINT b;
            GetCursorPos(out b);
            INPUT[] m = new INPUT[1];
            m[0].type = 0; m[0].mi.dx = 50; m[0].mi.dy = 50; m[0].mi.dwFlags = MOVE;
            uint n = SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(150);
            POINT c;
            GetCursorPos(out c);

            Console.WriteLine(string.Format(
                "[{0,5}ms 第{1}轮] SetCursorPos({2},{3}) ret={4,-5} 实际({5},{6}) {7}   SendInput ret={8} 位移({9},{10})",
                Environment.TickCount - t0, r, tx, ty, ret.ToString(), a.X, a.Y,
                hit ? "✅" : "❌", n, c.X - b.X, c.Y - b.Y));

            if (r < rounds) Thread.Sleep(2000);
        }

        Console.WriteLine();
        Console.WriteLine("可用轮数 " + everWorked + "/" + rounds);
        Console.WriteLine(everWorked > 0
            ? "结论：注入随进程存活时间变得可用 —— 说明需要预热或常驻。"
            : "结论：注入与进程存活时间无关 —— 全程不可用。");
        return 0;
    }
}
