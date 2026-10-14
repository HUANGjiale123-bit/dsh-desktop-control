using System;
using System.Runtime.InteropServices;

// 最小化的注入对比：只做一件事——把光标移到两个点，报告实际落点。
//
// 存在的理由：实测同一时刻、同一个 shell 里，
//   · PowerShell（Add-Type 内联 C#）→ SetCursorPos 成功、光标真的移动
//   · 编译出的 exe（本项目）        → SetCursorPos 返回 False、光标不动
// 这个差异本身很奇怪，需要一个最小程序来复现它，排除"程序太复杂"的可能。
//
// 输出刻意用纯 ASCII，避免 PowerShell 5.1 在中文/emoji 上的解析问题。
//
// 用法：MiniInject [日志文件]
class MiniInject
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int i, System.Text.StringBuilder b, uint len, out uint need);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError=true)] static extern int GetFileType(IntPtr h);

    static string ObjName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        var sb = new System.Text.StringBuilder(256); uint n;
        return GetUserObjectInformationW(h, 2, sb, 512, out n) ? sb.ToString() : "(err)";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    static System.Text.StringBuilder _log = new System.Text.StringBuilder();
    static string _file;

    static void L(string s)
    {
        Console.WriteLine(s);
        _log.AppendLine(s);
        try { System.IO.File.WriteAllText(_file, _log.ToString()); } catch { }
    }

    static int Main(string[] args)
    {
        _file = args.Length > 0 ? args[0] : @"D:\dsh workspace\dsh-desktop-control\.state\mini-inject.log";

        L("=== MiniInject ===");
        L("pid      : " + System.Diagnostics.Process.GetCurrentProcess().Id);
        L("before DPI aware: " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));

        bool aware = SetProcessDPIAware();
        System.Threading.Thread.Sleep(100);
        L("SetProcessDPIAware = " + aware);
        L("after  DPI aware: " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));

        POINT s;
        GetCursorPos(out s);
        L("start cursor: (" + s.X + "," + s.Y + ")");
        L("foreground  : " + GetForegroundWindow());
        L("winStation  : " + GetProcessWindowStation() + "  name=" + ObjName(GetProcessWindowStation()));
        L("threadDesk  : " + GetThreadDesktop(GetCurrentThreadId()) + "  name=" + ObjName(GetThreadDesktop(GetCurrentThreadId())));
        L("stdin type  : " + GetFileType(GetStdHandle(-10)));
        L("stdout type : " + GetFileType(GetStdHandle(-11)));
        L("stderr type : " + GetFileType(GetStdHandle(-12)));
        L("");

        int[,] pts = { { 700, 500 }, { 1200, 800 }, { 400, 300 } };
        int ok = 0;
        for (int i = 0; i < 3; i++)
        {
            int x = pts[i, 0], y = pts[i, 1];
            bool ret = SetCursorPos(x, y);
            System.Threading.Thread.Sleep(200);
            POINT a;
            GetCursorPos(out a);
            bool hit = (a.X == x && a.Y == y);
            if (hit) ok++;
            POINT tp = new POINT(); tp.X = x; tp.Y = y;
            L("SetCursorPos(" + x + "," + y + ") ret=" + ret + " actual=(" + a.X + "," + a.Y + ") " +
              (hit ? "OK" : "FAIL") + " windowAtTarget=" + WindowFromPoint(tp));
        }
        L("");
        L("result: " + ok + "/3");
        return 0;
    }
}
