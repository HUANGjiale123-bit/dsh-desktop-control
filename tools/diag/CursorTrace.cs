using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 高频光标轨迹记录器。
//
// 用途：让另一个 agent（如 Codex）执行"把鼠标移到某处"的动作时，
// 记录光标是否真的移动、走的是直线还是跳变、以及最终落点。
//
// 为什么需要它：我只能读到光标的**当前值**，读不到"有没有移动过"。
// 而判据必须是轨迹，不是终点——终点可能是巧合，轨迹不会。
//
// 每 50ms 采一次。只在位置变化时写一行，避免静止时刷屏。
//
// 用法：CursorTrace [秒数] [日志路径]
class CursorTrace
{
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    static string Title(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(80);
        GetWindowTextW(h, sb, 80);
        string t = sb.ToString();
        return t.Length > 26 ? t.Substring(0, 26) : t;
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int secs = args.Length > 0 ? int.Parse(args[0]) : 45;
        string log = args.Length > 1 ? args[1] : @"D:\dsh workspace\dsh-desktop-control\.state\cursortrace.log";
        try { if (File.Exists(log)) File.Delete(log); } catch { }

        var sb = new StringBuilder();
        Action<string> W = delegate (string s)
        {
            Console.WriteLine(s);
            try { File.AppendAllText(log, s + Environment.NewLine); } catch { }
        };

        W("=== 光标轨迹记录 ===");
        W("开始: " + DateTime.Now.ToString("HH:mm:ss.fff") + "   时长 " + secs + " 秒，采样 50ms");
        W("");

        POINT last;
        GetCursorPos(out last);
        W(string.Format("[{0,7}ms] 起点 ({1},{2})", 0, last.X, last.Y));

        long t0 = Environment.TickCount;
        int changes = 0;
        int maxJump = 0;
        POINT min = last, max = last;
        IntPtr lastFg = GetForegroundWindow();
        W(string.Format("[{0,7}ms] 前台 \"{1}\"", 0, Title(lastFg)));

        while (Environment.TickCount - t0 < secs * 1000L)
        {
            POINT p;
            if (!GetCursorPos(out p)) { Thread.Sleep(50); continue; }

            if (p.X != last.X || p.Y != last.Y)
            {
                changes++;
                int dx = p.X - last.X, dy = p.Y - last.Y;
                int jump = (int)Math.Sqrt((double)dx * dx + (double)dy * dy);
                if (jump > maxJump) maxJump = jump;

                if (p.X < min.X) min.X = p.X;
                if (p.Y < min.Y) min.Y = p.Y;
                if (p.X > max.X) max.X = p.X;
                if (p.Y > max.Y) max.Y = p.Y;

                // 只在变化较大或每 10 次变化时写一行，避免刷屏
                if (jump >= 8 || changes % 10 == 0)
                {
                    IntPtr at = WindowFromPoint(p);
                    uint pid;
                    GetWindowThreadProcessId(at, out pid);
                    W(string.Format("[{0,7}ms] ({1,5},{2,5})  位移({3,5},{4,5}) 跳{5,4}px  处=[{6}] pid={7}",
                        Environment.TickCount - t0, p.X, p.Y, dx, dy, jump, Title(at), pid));
                }
                last = p;
            }

            IntPtr fg = GetForegroundWindow();
            if (fg != lastFg)
            {
                lastFg = fg;
                W(string.Format("[{0,7}ms] ★前台变化 → \"{1}\"", Environment.TickCount - t0, Title(fg)));
            }

            Thread.Sleep(50);
        }

        POINT e;
        GetCursorPos(out e);
        W("");
        W("=== 汇总 ===");
        W("  位置变化次数 : " + changes);
        W("  最大单次跳变 : " + maxJump + " px");
        W("  活动范围     : x " + min.X + "~" + max.X + "   y " + min.Y + "~" + max.Y);
        W("  最终位置     : (" + e.X + "," + e.Y + ")");
        W("");
        W(changes > 0
            ? "判读：光标有移动 —— 记录到了轨迹。"
            : "判读：光标全程未动 —— 该时段内没有任何鼠标移动。");
        return 0;
    }
}
