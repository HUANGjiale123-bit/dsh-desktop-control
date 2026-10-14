using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class StateProbe
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gti);
    [DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")]
    static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")]
    static extern IntPtr GetFocus();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    static string Title(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(120);
        GetWindowTextW(h, sb, 120);
        var cn = new StringBuilder(120);
        GetClassNameW(h, cn, 120);
        return sb.ToString() + " <" + cn.ToString() + ">";
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int secs = args.Length > 0 ? int.Parse(args[0]) : 20;

        Console.WriteLine("=== 只读状态探针（不做任何注入）" + secs + " 秒 ===");
        Console.WriteLine();

        DateTime t0 = DateTime.Now;
        IntPtr lastFg = IntPtr.Zero;
        int changes = 0;

        while ((DateTime.Now - t0).TotalSeconds < secs)
        {
            IntPtr fg = GetForegroundWindow();
            uint pid;
            GetWindowThreadProcessId(fg, out pid);

            // 关键：前台线程的 GUI 状态（有无捕获、有无菜单、焦点在哪）
            uint fgThread = (uint)GetWindowThreadProcessId(fg, IntPtr.Zero);
            GUITHREADINFO gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            bool gtiOk = GetGUIThreadInfo(fgThread, ref gti);

            POINT c;
            GetCursorPos(out c);

            string mark = "";
            if (fg != lastFg)
            {
                changes++;
                mark = "   ← 前台变化 #" + changes;
                lastFg = fg;
            }

            Console.WriteLine(string.Format(
                "[{0,2}s] 前台={1,-8} [{2}] 捕获={3,-8} 菜单={4,-8} 光标=({5},{6}){7}",
                (int)(DateTime.Now - t0).TotalSeconds,
                fg.ToString(),
                Title(fg).Substring(0, Math.Min(30, Title(fg).Length)),
                gtiOk ? gti.hwndCapture.ToString() : "?",
                gtiOk ? gti.hwndMenuOwner.ToString() : "?",
                c.X, c.Y, mark));

            Thread.Sleep(1000);
        }
        Console.WriteLine();
        Console.WriteLine("前台共变化 " + changes + " 次");
        return 0;
    }
}
