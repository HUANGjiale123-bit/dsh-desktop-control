// 用裸 CreateProcessW 启动 PowerShell，绕开 .NET 的 Process.Start。
//
// ── 为什么 ──
//
//   python.exe（非 .NET）→ 读 39
//   JobTest.exe（.NET，Process.Start）→ 读 5
//   python.exe 同样在 job 里 → job 已排除
//
// 那么差异要么在 Process.Start 这个封装，要么在 .NET 运行时本身。
// 这个程序用 CreateProcessW 直接起（.NET 运行时还在，但不走 Process.Start），
// 就能把两者分开。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class RawCreate
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(
        string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment,
        string lpCurrentDirectory, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [STAThread]
    static void Main(string[] args)
    {
        string script = args[0];
        // 用 cmd /c 包一层：CreateProcessW 直启 powershell 时，命令行里的 "> file"
        // 不会被任何人解析（那是 cmd 的语法），所以重定向必须交给 cmd。
        string cmd = "C:\\Windows\\System32\\cmd.exe /c \"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" +
                     " -NoProfile -ExecutionPolicy Bypass -File \\\"" + script + "\\\"\"";

        // 把子进程的 stdout 重定向到一个工作区文件，这样能读到内容
        string outFile = Path.Combine(Path.GetDirectoryName(script), "raw-out.txt");
        cmd += " > \"" + outFile + "\"";

        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        PROCESS_INFORMATION pi;

        // CREATE_NO_WINDOW = 0x08000000
        bool ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
            0x08000000, IntPtr.Zero, null, ref si, out pi);
        Console.WriteLine("[CreateProcessW] 成功=" + ok);
        if (!ok)
        {
            Console.WriteLine("  错误码: " + Marshal.GetLastWin32Error());
            return;
        }

        WaitForSingleObject(pi.hProcess, 60000);
        uint code;
        GetExitCodeProcess(pi.hProcess, out code);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        Console.WriteLine("  退出码: " + code);

        if (File.Exists(outFile))
        {
            string text = File.ReadAllText(outFile);
            Console.WriteLine("  输出字节数: " + text.Length);
            int idx = 0;
            var ns = new System.Collections.Generic.List<string>();
            while (true)
            {
                int a = text.IndexOf("\"nodes\":", idx);
                if (a < 0) break;
                int b = a + 8, e = b;
                while (e < text.Length && (char.IsDigit(text[e]) || text[e] == '-' || text[e] == ' ')) e++;
                ns.Add(text.Substring(b, e - b).Trim());
                idx = e;
            }
            Console.WriteLine("  nodes: " + string.Join(" / ", ns.ToArray()));
        }
        else
        {
            Console.WriteLine("  没生成输出文件");
        }
    }
}
