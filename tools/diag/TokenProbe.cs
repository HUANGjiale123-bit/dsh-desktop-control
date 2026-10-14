using System;
using System.Runtime.InteropServices;
using System.Text;

class TokenProbe
{
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr t, int c, IntPtr i, int l, out int r);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint a, bool i, uint p);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();

    const uint TOKEN_QUERY = 0x0008;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // TokenInformationClass
    const int TokenIsAppContainer = 29;
    const int TokenAppContainerSid = 31;
    const int TokenIsAppSilo = 49;
    const int TokenIntegrityLevel = 25;
    const int TokenElevationType = 18;
    const int TokenUIAccess = 26;
    const int TokenSessionId = 12;
    const int TokenVirtualizationAllowed = 23;
    const int TokenRestrictedSids = 11;

    static string Report(uint pid, IntPtr procHandle)
    {
        var sb = new StringBuilder();
        sb.AppendLine("── pid " + pid + " ──");

        IntPtr tok;
        if (!OpenProcessToken(procHandle, TOKEN_QUERY, out tok))
        {
            int e = Marshal.GetLastWin32Error();
            sb.AppendLine("   OpenProcessToken 失败 err=" + e + " (" +
                new System.ComponentModel.Win32Exception(e).Message + ")");
            return sb.ToString();
        }

        IntPtr buf = Marshal.AllocHGlobal(2048);
        try
        {
            int ret;
            int iv;

            if (GetTokenInformation(tok, TokenIsAppContainer, buf, 2048, out ret))
            {
                iv = Marshal.ReadInt32(buf);
                sb.AppendLine("   AppContainer        = " + (iv != 0 ? "是 ⚠️（沙箱容器，无法注入输入）" : "否"));
            }
            else sb.AppendLine("   AppContainer 查询失败 err=" + Marshal.GetLastWin32Error());

            if (GetTokenInformation(tok, TokenIsAppSilo, buf, 2048, out ret))
            {
                iv = Marshal.ReadInt32(buf);
                sb.AppendLine("   AppSilo             = " + (iv != 0 ? "是" : "否"));
            }

            if (GetTokenInformation(tok, TokenElevationType, buf, 2048, out ret))
            {
                iv = Marshal.ReadInt32(buf);
                string[] names = { "?", "Default(未提升)", "Full(已提升)", "Limited" };
                sb.AppendLine("   ElevationType       = " + iv + " " + (iv >= 1 && iv <= 3 ? names[iv] : ""));
            }

            if (GetTokenInformation(tok, TokenSessionId, buf, 2048, out ret))
                sb.AppendLine("   会话 id             = " + Marshal.ReadInt32(buf));

            if (GetTokenInformation(tok, TokenUIAccess, buf, 2048, out ret))
            {
                iv = Marshal.ReadInt32(buf);
                sb.AppendLine("   UIAccess            = " + (iv != 0 ? "是" : "否"));
            }

            // 完整性级别
            if (GetTokenInformation(tok, TokenIntegrityLevel, buf, 2048, out ret))
            {
                IntPtr sid = Marshal.ReadIntPtr(buf);   // SID_AND_ATTRIBUTES.Sid
                byte cnt;
                if (GetSidSubAuthorityCount(sid, out cnt) && cnt > 0)
                {
                    IntPtr p = GetSidSubAuthority(sid, (uint)(cnt - 1));
                    uint rid = (uint)Marshal.ReadInt32(p);
                    string lvl;
                    if (rid < 0x1000) lvl = "Untrusted";
                    else if (rid < 0x2000) lvl = "Low";
                    else if (rid < 0x3000) lvl = "Medium";
                    else if (rid < 0x4000) lvl = "Medium-Plus";
                    else if (rid < 0x5000) lvl = "High(管理员)";
                    else lvl = "System";
                    sb.AppendLine("   完整性级别          = " + lvl + "  (RID=0x" + rid.ToString("X") + ")");
                }
            }
            else sb.AppendLine("   完整性级别查询失败 err=" + Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            CloseHandle(tok);
        }
        return sb.ToString();
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSidSubAuthorityCount(IntPtr sid, out byte count);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 令牌检查：查 DSH 及其子进程是否被沙箱化 ===");
        Console.WriteLine();
        Console.Write(Report(GetCurrentProcessId(), GetCurrentProcess()));

        foreach (string a in args)
        {
            uint pid;
            if (!uint.TryParse(a, out pid)) continue;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                Console.WriteLine("── pid " + pid + " ──");
                Console.WriteLine("   OpenProcess 失败 err=" + e + " (" +
                    new System.ComponentModel.Win32Exception(e).Message + ")");
                continue;
            }
            Console.Write(Report(pid, h));
            CloseHandle(h);
        }
        return 0;
    }
}
