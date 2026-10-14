using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// 双变量对照的自动化版本：一次跑完，输出结构化报告。
//
// 背景（2026-10-14）：关于"为什么工作区里的 exe 不能注入"，有两个变量被混在一起：
//   · 启动者（agent 的沙箱 shell vs Codex helper vs explorer）
//   · 可执行文件所在路径（D:\dsh workspace\ vs 其他）
//
// 本程序只负责把**当下这一次运行**的全部可观测事实记录下来：
//   · 自己是哪个文件、md5、在哪个目录
//   · 父进程链
//   · 窗口站 / 桌面 / 会话
//   · 起始光标读数（区分"能看见交互式桌面"与"看不见"）
//   · 注入三次的结果（判据是光标实际位置）
//
// 用法：InjectProbe <输出文件>
//
// 设计要点：**输出用纯 ASCII + 单行 key=value**，便于跨进程比对与机器判读，
// 也避开 PowerShell 5.1 在中文/emoji 上的解析问题（本项目已在这上面栽过两次）。
class InjectProbe
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
    [DllImport("user32.dll")]
    static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int i, StringBuilder b, uint len, out uint need);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();

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

    static string ObjName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(256); uint n;
        return GetUserObjectInformationW(h, 2, sb, 512, out n) ? sb.ToString() : "(err)";
    }

    static int Main(string[] args)
    {
        string outFile = args.Length > 0 ? args[0] : @"D:\injectprobe.log";
        var R = new StringBuilder();
        Action<string, string> KV = delegate (string k, string v) { R.AppendLine(k + "=" + v); };

        // ── 身份
        string self = "";
        try { self = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch { }
        KV("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        KV("pid", GetCurrentProcessId().ToString());
        KV("exePath", self);
        KV("exeDir", string.IsNullOrEmpty(self) ? "?" : Path.GetDirectoryName(self));
        try
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var fs = File.OpenRead(self))
            {
                byte[] h = md5.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                KV("exeMd5", sb.ToString());
            }
        }
        catch { KV("exeMd5", "unavailable"); }
        KV("inWorkspace", self.IndexOf(@"\dsh workspace\", StringComparison.OrdinalIgnoreCase) >= 0 ? "YES" : "no");

        // ── 父进程链
        try
        {
            var chain = new StringBuilder();
            uint pid = GetCurrentProcessId();
            for (int i = 0; i < 8; i++)
            {
                var p = System.Diagnostics.Process.GetProcessById((int)pid);
                chain.Append(p.ProcessName);
                // 用 WMI 拿父 pid 代价高，这里用 .NET 拿不到父 pid；
                // 父链由外部 PowerShell 补，本程序只记自己
                break;
            }
            KV("selfName", chain.ToString());
        }
        catch { }

        // ── 环境
        bool aware = SetProcessDPIAware();
        System.Threading.Thread.Sleep(120);
        KV("dpiAware", aware ? "ok" : "fail");
        KV("screen", GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        KV("winStation", GetProcessWindowStation() + " name=" + ObjName(GetProcessWindowStation()));
        IntPtr td = GetThreadDesktop(GetCurrentThreadId());
        KV("threadDesktop", td + " name=" + ObjName(td));

        POINT s;
        GetCursorPos(out s);
        KV("startCursor", s.X + "," + s.Y);
        KV("cursorReadable", (s.X != 0 || s.Y != 0) ? "YES" : "NO(0,0)");

        // ── 注入测试
        int ok = 0;
        int[,] pts = { { 800, 450 }, { 1300, 700 }, { 500, 300 } };
        for (int i = 0; i < 3; i++)
        {
            int x = pts[i, 0], y = pts[i, 1];
            bool ret = SetCursorPos(x, y);
            System.Threading.Thread.Sleep(180);
            POINT a;
            GetCursorPos(out a);
            bool hit = (a.X == x && a.Y == y);
            if (hit) ok++;
            KV("setCursorPos" + (i + 1), "target=" + x + "," + y + " ret=" + ret + " actual=" + a.X + "," + a.Y + " " + (hit ? "HIT" : "MISS"));
        }

        POINT b4;
        GetCursorPos(out b4);
        INPUT[] m = new INPUT[1];
        m[0].type = 0; m[0].mi.dx = 50; m[0].mi.dy = 50; m[0].mi.dwFlags = MOVE;
        uint n = SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(180);
        POINT a4;
        GetCursorPos(out a4);
        KV("sendInputRelative", "ret=" + n + " moved=" + (a4.X - b4.X) + "," + (a4.Y - b4.Y));

        KV("RESULT", ok + "/3 " + (ok > 0 ? "INJECTION_WORKS" : "INJECTION_DEAD"));

        try { File.WriteAllText(outFile, R.ToString()); } catch { }
        Console.Write(R.ToString());
        return 0;
    }
}
