using System;
using System.Runtime.InteropServices;
using System.Threading;

class InjectMonitor
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    static string Title(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(160);
        GetWindowTextW(h, sb, 160);
        return sb.ToString();
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        SetProcessDPIAware();
        int secs = args.Length > 0 ? int.Parse(args[0]) : 30;

        Console.WriteLine("=== 注入能力连续监视（" + secs + " 秒，每秒一次）===");
        Console.WriteLine("每秒尝试 SetCursorPos 到一个交替坐标，记录成功/失败。");
        Console.WriteLine("请在此期间【动一动鼠标、点一下窗口】，看它会不会恢复。");
        Console.WriteLine();

        int lastOk = -1;
        DateTime t0 = DateTime.Now;
        int i = 0;
        while ((DateTime.Now - t0).TotalSeconds < secs)
        {
            i++;
            // 交替目标，避免"已经在该点"造成的假成功
            int tx = (i % 2 == 0) ? 300 : 1500;
            int ty = (i % 2 == 0) ? 200 : 900;

            POINT b; GetCursorPos(out b);
            bool ok = SetCursorPos(tx, ty);
            Thread.Sleep(120);
            POINT a; GetCursorPos(out a);
            bool moved = (a.X == tx && a.Y == ty);

            string state = moved ? "✅ 可注入" : "❌ 被拦";
            int nowOk = moved ? 1 : 0;

            string mark = "";
            if (lastOk != -1 && nowOk != lastOk)
                mark = nowOk == 1 ? "   ★★★ 刚刚恢复！" : "   ☆☆☆ 刚刚失效！";
            lastOk = nowOk;

            Console.WriteLine(string.Format("[{0,2}s] SetCursorPos({1,4},{2,4})={3,-5} 实际=({4,4},{5,4}) {6}  前台=[{7}]{8}",
                (int)(DateTime.Now - t0).TotalSeconds, tx, ty, ok.ToString(), a.X, a.Y, state,
                Title(GetForegroundWindow()).Substring(0, Math.Min(28, Title(GetForegroundWindow()).Length)), mark));

            Thread.Sleep(880);
        }
        Console.WriteLine();
        Console.WriteLine("结束。");
        return 0;
    }
}
