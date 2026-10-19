// 验证假设：父进程调用 SetProcessDPIAware() 后，它启动的 powershell.exe
// 就读不到 Chromium 的 UIA 树了。
//
// ── 为什么怀疑它 ──
//
// dsc.exe 的 Main 第一行就是 SetProcessDPIAware()，而它启动的 PowerShell
// 读到 5 个节点；从 bash 直接启动的 PowerShell 读到 39 个。
// 同一个脚本、同一个 powershell.exe、同一个窗口。
//
// 这个程序只做一件事：先按参数决定要不要调 SetProcessDPIAware，
// 然后启动同一个脚本，把输出打出来。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

class DpiTest
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    static void Main(string[] args)
    {
        string script = args[0];
        bool setDpi = args.Length > 1 && args[1] == "dpi";

        if (setDpi)
        {
            bool ok = SetProcessDPIAware();
            Console.WriteLine("[父进程] SetProcessDPIAware() = " + ok);
        }
        else
        {
            Console.WriteLine("[父进程] 不调 SetProcessDPIAware");
        }

        var psi = new ProcessStartInfo();
        psi.FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        string extra = args.Length > 4 ? (" " + args[4]) : "";
        psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"" + extra;
        psi.UseShellExecute = false;
        bool inherit = args.Length > 2 && args[2] == "inherit";
        if (!inherit)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        // 假设：CreateNoWindow 会传 CREATE_NO_WINDOW，可能影响子进程的桌面/窗口站访问
        psi.CreateNoWindow = !(args.Length > 3 && args[3] == "window");

        if (inherit)
        {
            Console.WriteLine("[模式] 继承 stdout");
            using (var p0 = Process.Start(psi)) { p0.WaitForExit(); }
            return;
        }

        using (var p = Process.Start(psi))
        {
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            // 只抽每个 hwnd 的 nodes 数字，避免刷屏
            Console.WriteLine("[子进程 stdout 前 400 字]");
            Console.WriteLine(stdout.Length > 400 ? stdout.Substring(0, 400) : stdout);
            if (stderr.Length > 0)
                Console.WriteLine("[stderr] " + (stderr.Length > 200 ? stderr.Substring(0, 200) : stderr));
        }
    }
}
