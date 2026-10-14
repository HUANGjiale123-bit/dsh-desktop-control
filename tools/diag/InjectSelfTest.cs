using System;
using System.IO;
using System.Runtime.InteropServices;

// 注入能力自测：把结果写到文件。
//
// 为什么要写文件而不是打印：这个程序需要从**不同的父进程**启动
// （例如让 explorer 启动它，模拟"用户双击"），那种情况下 stdout 回不来。
//
// 用法：InjectSelfTest <输出文件>
class InjectSelfTest
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
    static extern int GetSystemMetrics(int i);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

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
    const uint MOVE = 0x0001, ABSOLUTE = 0x8000;

    static int Main(string[] args)
    {
        string outFile = args.Length > 0 ? args[0] : @"D:\dsh workspace\dsh-desktop-control\.state\inject-selftest.log";
        var sb = new System.Text.StringBuilder();
        Action<string> W = delegate (string s) { sb.AppendLine(s); };

        SetProcessDPIAware();
        System.Threading.Thread.Sleep(120);

        W("=== 注入自测 ===");
        W("时间   : " + DateTime.Now.ToString("HH:mm:ss.fff"));
        W("pid    : " + System.Diagnostics.Process.GetCurrentProcess().Id);
        W("屏幕   : " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));

        // 父进程：用于确认这次是从哪儿启动的
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            W("父进程 : (见下)");
        }
        catch { }

        POINT start;
        GetCursorPos(out start);
        W("起始光标: (" + start.X + "," + start.Y + ")");
        W("");

        int tx = 800, ty = 450;
        int arrived = 0;

        // 测 3 次，每次换目标，只看光标实际位置
        int[,] pts = { { 800, 450 }, { 1300, 700 }, { 500, 300 } };
        for (int i = 0; i < 3; i++)
        {
            int x = pts[i, 0], y = pts[i, 1];
            bool ret = SetCursorPos(x, y);
            System.Threading.Thread.Sleep(200);
            POINT a;
            GetCursorPos(out a);
            bool hit = (a.X == x && a.Y == y);
            if (hit) arrived++;
            W("SetCursorPos(" + x + "," + y + ") ret=" + ret + " → 实际(" + a.X + "," + a.Y + ") " + (hit ? "✅" : "❌"));
        }

        // SendInput 相对
        POINT b4;
        GetCursorPos(out b4);
        INPUT[] m = new INPUT[1];
        m[0].type = 0; m[0].mi.dx = 40; m[0].mi.dy = 40; m[0].mi.dwFlags = MOVE;
        uint n = SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(200);
        POINT a4;
        GetCursorPos(out a4);
        W("SendInput 相对(+40,+40) ret=" + n + " → 位移(" + (a4.X - b4.X) + "," + (a4.Y - b4.Y) + ")");

        W("");
        W("到达 " + arrived + "/3");
        W(arrived > 0 ? "结论：注入可用" : "结论：注入不可用");

        try { File.WriteAllText(outFile, sb.ToString()); } catch { }
        Console.WriteLine(sb.ToString());
        return 0;
    }
}
