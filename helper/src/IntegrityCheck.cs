/*
 * IntegrityCheck —— 打印进程的完整性级别与其他安全特征
 *
 * 目的：验证一个具体假设——DeepSeek Harness（及其子进程）与 ChatGPT/Codex
 * 的进程处在不同的完整性级别上。若 DSH 是高完整性（管理员），则它拉起的子进程
 * 安装低层钩子会静默失效：SetWindowsHookExW 返回非零句柄，但回调永不触发。
 * 这正好解释了实测到的全部现象。
 *
 * 同时打印令牌的其它关键位（AppContainer、是否提升、会话 id），
 * 便于与 Codex 的 helper 逐项对比。
 *
 * 用法：dsc-integrity [pid ...]
 *   不带参数时检查自己；带 pid 时检查那些进程。
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

class IntegrityCheck
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSidSubAuthorityCount(IntPtr sid, out byte count);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint TOKEN_QUERY = 0x0008;
    const int TokenIntegrityLevel = 25;
    const int TokenElevation = 20;
    const int TokenIsAppContainer = 29;
    const int TokenSessionId = 12;

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_MANDATORY_LABEL
    {
        public IntPtr Label;   // SID_AND_ATTRIBUTES
    }

    static string IntegrityName(uint rid)
    {
        if (rid < 0x1000) return "Untrusted(0x" + rid.ToString("X") + ")";
        if (rid < 0x2000) return "Low(0x" + rid.ToString("X") + ")";
        if (rid < 0x3000) return "Medium(0x" + rid.ToString("X") + ")";
        if (rid < 0x4000) return "Medium-Plus(0x" + rid.ToString("X") + ")";
        if (rid < 0x5000) return "High(0x" + rid.ToString("X") + ")  ← 管理员/已提升";
        return "System(0x" + rid.ToString("X") + ")";
    }

    static string Report(uint pid, IntPtr procHandle, bool owned)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("── pid " + pid + " ──");

        IntPtr tok;
        if (!OpenProcessToken(procHandle, TOKEN_QUERY, out tok))
        {
            int e = Marshal.GetLastWin32Error();
            sb.AppendLine("   OpenProcessToken 失败 err=" + e + " (" +
                new System.ComponentModel.Win32Exception(e).Message + ")");
            return sb.ToString();
        }

        try
        {
            int ret;
            IntPtr buf = Marshal.AllocHGlobal(1024);
            try
            {
                // 完整性级别
                if (GetTokenInformation(tok, TokenIntegrityLevel, buf, 1024, out ret))
                {
                    TOKEN_MANDATORY_LABEL tml = (TOKEN_MANDATORY_LABEL)Marshal.PtrToStructure(buf, typeof(TOKEN_MANDATORY_LABEL));
                    IntPtr sid = tml.Label;   // SID_AND_ATTRIBUTES { Sid, Attributes }
                    byte subCount;
                    if (GetSidSubAuthorityCount(sid, out subCount) && subCount > 0)
                    {
                        IntPtr p = GetSidSubAuthority(sid, (uint)(subCount - 1));
                        uint rid = (uint)Marshal.ReadInt32(p);
                        sb.AppendLine("   完整性级别 = " + IntegrityName(rid) + "   (RID=0x" + rid.ToString("X") + ")");
                    }
                }
                else
                {
                    sb.AppendLine("   TokenIntegrityLevel 查询失败 err=" + Marshal.GetLastWin32Error());
                }

                // 是否提升
                if (GetTokenInformation(tok, TokenElevation, buf, 1024, out ret))
                {
                    int elev = Marshal.ReadInt32(buf);
                    sb.AppendLine("   已提升(TokenElevation) = " + (elev != 0 ? "是 ⚠️" : "否"));
                }

                // 会话
                if (GetTokenInformation(tok, TokenSessionId, buf, 1024, out ret))
                    sb.AppendLine("   会话 id = " + Marshal.ReadInt32(buf));

                // AppContainer
                if (GetTokenInformation(tok, TokenIsAppContainer, buf, 1024, out ret))
                    sb.AppendLine("   AppContainer = " + (Marshal.ReadInt32(buf) != 0 ? "是" : "否"));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(tok); if (owned) CloseHandle(procHandle); }

        return sb.ToString();
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 完整性级别检查 ===");
        Console.WriteLine();

        Console.WriteLine("【本进程】");
        Console.Write(Report(GetCurrentProcessId(), GetCurrentProcess(), false));

        if (args.Length > 0)
        {
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
                Console.Write(Report(pid, h, true));
            }
        }
        return 0;
    }
}
