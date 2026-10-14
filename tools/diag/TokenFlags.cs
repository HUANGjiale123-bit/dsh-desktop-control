using System;
using System.Runtime.InteropServices;
using System.Text;

// 读取当前进程令牌的关键位，用来判断是否处在 Windows 受限令牌下。
//
// 背景：DSH 在 Windows 上的沙箱后端是 @deepseek-ai/dsh-sandbox-windows-acl，
// 它用 **WRITE_RESTRICTED 令牌** + capability-SID 写白名单来限制子进程。
// 而 WRITE_RESTRICTED 不只限制文件写入——它对**窗口对象的写操作**同样生效，
// 这会静默影响 SetForegroundWindow / SetCursorPos / SendInput 这类输入注入。
//
// TokenFlags 里的位（winnt.h）：
//   0x0004  TOKEN_VIRTUALIZE_BITMAP
//   0x0008  TOKEN_INTEGRITY_LEVEL
//   0x0010  TOKEN_IS_RESTRICTED        ← 受限令牌
//   0x0020  TOKEN_APPCONTAINER_TOKEN
//   0x0100  TOKEN_WRITE_RESTRICTED     ← ★ 关键位
//
// 用法：TokenFlags [输出文件]
class TokenFlags
{
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr t, int cls, IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSidSubAuthorityCount(IntPtr sid, out byte count);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr str);

    const uint TOKEN_QUERY = 0x0008;
    const int TokenFlagsClass = 32;            // TokenFlags
    const int TokenRestrictedSidsClass = 11;   // TokenRestrictedSids
    const int TokenIntegrityLevelClass = 25;

    static string Sid(IntPtr p)
    {
        IntPtr s;
        if (!ConvertSidToStringSidW(p, out s)) return "(?)";
        string r = Marshal.PtrToStringUni(s) ?? "(?)";
        return r;
    }

    static string DescribeFlags(uint f)
    {
        var sb = new StringBuilder();
        sb.Append("0x").Append(f.ToString("X4"));
        if ((f & 0x0010) != 0) sb.Append("  TOKEN_IS_RESTRICTED");
        if ((f & 0x0100) != 0) sb.Append("  ★TOKEN_WRITE_RESTRICTED");
        if ((f & 0x0020) != 0) sb.Append("  TOKEN_APPCONTAINER");
        if ((f & 0x0008) != 0) sb.Append("  TOKEN_INTEGRITY_LEVEL");
        if ((f & 0x0004) != 0) sb.Append("  TOKEN_VIRTUALIZE_BITMAP");
        return sb.ToString();
    }

    static void Main(string[] args)
    {
        string outFile = args.Length > 0 ? args[0] : @"D:\tokenflags.log";
        var R = new StringBuilder();
        Action<string, string> KV = (k, v) => R.AppendLine(k + "=" + v);

        KV("pid", GetCurrentProcessId().ToString());
        KV("exe", System.Reflection.Assembly.GetExecutingAssembly().Location);

        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out tok))
        {
            KV("RESULT", "OpenProcessToken failed err=" + Marshal.GetLastWin32Error());
            Console.Write(R.ToString());
            try { System.IO.File.WriteAllText(outFile, R.ToString()); } catch { }
            return;
        }

        IntPtr buf = Marshal.AllocHGlobal(4096);
        try
        {
            int ret;

            // TokenFlags
            if (GetTokenInformation(tok, TokenFlagsClass, buf, 4096, out ret))
            {
                uint flags = (uint)Marshal.ReadInt32(buf);
                KV("tokenFlags", DescribeFlags(flags));
                KV("WRITE_RESTRICTED", (flags & 0x0100) != 0 ? "YES" : "no");
                KV("IS_RESTRICTED", (flags & 0x0010) != 0 ? "YES" : "no");
            }
            else KV("tokenFlags", "query failed err=" + Marshal.GetLastWin32Error());

            // 受限 SID 列表（capability SID 就在这里）
            if (GetTokenInformation(tok, TokenRestrictedSidsClass, buf, 4096, out ret))
            {
                int count = Marshal.ReadInt32(buf);
                KV("restrictedSidCount", count.ToString());
                // TOKEN_GROUPS: DWORD GroupCount; SID_AND_ATTRIBUTES Groups[];
                // SID_AND_ATTRIBUTES 在 x64 下是 16 字节（指针 8 + DWORD 4 + 对齐 4）
                IntPtr baseAddr = new IntPtr(buf.ToInt64() + 8);
                for (int i = 0; i < count && i < 12; i++)
                {
                    IntPtr sidPtr = Marshal.ReadIntPtr(baseAddr, i * 16);
                    KV("restrictedSid[" + i + "]", Sid(sidPtr));
                }
            }
            else KV("restrictedSidCount", "query failed err=" + Marshal.GetLastWin32Error());

            // 完整性级别
            if (GetTokenInformation(tok, TokenIntegrityLevelClass, buf, 4096, out ret))
            {
                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte cnt;
                if (GetSidSubAuthorityCount(sid, out cnt) && cnt > 0)
                {
                    IntPtr p = GetSidSubAuthority(sid, (uint)(cnt - 1));
                    uint rid = (uint)Marshal.ReadInt32(p);
                    KV("integrityRid", "0x" + rid.ToString("X"));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        Console.Write(R.ToString());
        try { System.IO.File.WriteAllText(outFile, R.ToString()); } catch { }
    }
}
