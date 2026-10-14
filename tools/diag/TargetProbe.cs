using System;
using System.Runtime.InteropServices;
using System.Text;

class TargetProbe
{
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr t, int c, IntPtr i, int l, out int r);
    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint a, bool i, uint p);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSidSubAuthorityCount(IntPtr sid, out byte count);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

    delegate bool EnumProc(IntPtr h, IntPtr p);

    const uint TOKEN_QUERY = 0x0008;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    static string Integrity(IntPtr tok)
    {
        IntPtr buf = Marshal.AllocHGlobal(1024);
        try
        {
            int ret;
            if (!GetTokenInformation(tok, 25 /*TokenIntegrityLevel*/, buf, 1024, out ret))
                return "查询失败";
            IntPtr sid = Marshal.ReadIntPtr(buf);
            byte cnt;
            if (!GetSidSubAuthorityCount(sid, out cnt) || cnt == 0) return "?";
            IntPtr p = GetSidSubAuthority(sid, (uint)(cnt - 1));
            uint rid = (uint)Marshal.ReadInt32(p);
            string lvl;
            if (rid < 0x1000) lvl = "Untrusted";
            else if (rid < 0x2000) lvl = "Low";
            else if (rid < 0x3000) lvl = "Medium";
            else if (rid < 0x4000) lvl = "Medium-Plus";
            else if (rid < 0x5000) lvl = "High";
            else lvl = "System";
            return lvl + " (0x" + rid.ToString("X") + ")";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string Probe(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return "OpenProcess 失败 err=" + Marshal.GetLastWin32Error();

        IntPtr tok;
        if (!OpenProcessToken(h, TOKEN_QUERY, out tok))
        {
            int e = Marshal.GetLastWin32Error();
            CloseHandle(h);
            return "OpenProcessToken 失败 err=" + e + " (更高级别?)";
        }

        var sb = new StringBuilder();
        sb.Append("完整性=" + Integrity(tok));

        IntPtr buf = Marshal.AllocHGlobal(1024);
        try
        {
            int ret, v;
            if (GetTokenInformation(tok, 29 /*TokenIsAppContainer*/, buf, 1024, out ret))
            {
                v = Marshal.ReadInt32(buf);
                sb.Append("  AppContainer=" + (v != 0 ? "是" : "否"));
            }
            if (GetTokenInformation(tok, 18 /*TokenElevationType*/, buf, 1024, out ret))
            {
                v = Marshal.ReadInt32(buf);
                sb.Append("  提升类型=" + v);
            }
            if (GetTokenInformation(tok, 26 /*TokenUIAccess*/, buf, 1024, out ret))
            {
                v = Marshal.ReadInt32(buf);
                sb.Append("  UIAccess=" + (v != 0 ? "是" : "否"));
            }
            if (GetTokenInformation(tok, 12 /*TokenSessionId*/, buf, 1024, out ret))
                sb.Append("  会话=" + Marshal.ReadInt32(buf));
        }
        finally { Marshal.FreeHGlobal(buf); }

        CloseHandle(tok);
        CloseHandle(h);
        return sb.ToString();
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 目标进程特征对比 ===");
        Console.WriteLine();

        Console.WriteLine("【我自己】");
        Console.WriteLine("  " + Probe((uint)System.Diagnostics.Process.GetCurrentProcess().Id));
        Console.WriteLine();

        Console.WriteLine("【各可见窗口所属进程】");
        var seen = new System.Collections.Generic.HashSet<uint>();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (!IsWindowVisible(h)) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == 0 || !seen.Add(pid)) return true;
            var sb = new StringBuilder(120);
            GetWindowTextW(h, sb, 120);
            string t = sb.ToString();
            if (t.Length == 0) return true;
            Console.WriteLine("  pid=" + pid.ToString().PadRight(7) + "[" + t.Substring(0, Math.Min(24, t.Length)).PadRight(24) + "]  " + Probe(pid));
            return true;
        }, IntPtr.Zero);

        IntPtr fg = GetForegroundWindow();
        uint fpid;
        GetWindowThreadProcessId(fg, out fpid);
        Console.WriteLine();
        Console.WriteLine("前台窗口 pid = " + fpid);
        return 0;
    }
}
