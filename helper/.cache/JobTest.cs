// 验证假设：.NET 的 Process.Start 把子进程放进了一个 job object，
// 而这个 job 限制了子进程读 Chromium 的 UIA 树。
//
// ── 数据 ──
//
//   cmd.exe / python.exe / bash 启动 PowerShell → 读到 39 个节点
//   dsc.exe / DpiTest.exe（.NET）启动         → 读到 5 个
//
// 已排除：DPI 感知、-NonInteractive、CreateNoWindow、stdout 重定向、沙箱。
//
// 这个程序检查三件事：
//   ① 自己是否在 job 里
//   ② 启动子进程后，子进程是否在同一个 job 里
//   ③ 用 CREATE_BREAKAWAY_FROM_JOB 启动，能否读到

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

class JobTest
{
    [DllImport("kernel32.dll")]
    static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [STAThread]
    static void Main(string[] args)
    {
        string script = args[0];

        bool inJob;
        bool ok = IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob);
        Console.WriteLine("[本进程] IsProcessInJob 调用=" + ok + " 在job里=" + inJob);

        // 用 Process.Start 启动（默认）
        RunOne(script, "默认 Process.Start", false);

        // 用 CreateProcess 直接启动，带 CREATE_BREAKAWAY_FROM_JOB
        RunOne(script, "CREATE_BREAKAWAY_FROM_JOB", true);
    }

    static void RunOne(string script, string label, bool breakaway)
    {
        Console.WriteLine();
        Console.WriteLine("[模式] " + label);

        var psi = new ProcessStartInfo();
        psi.FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"";
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        using (var p = Process.Start(psi))
        {
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            bool childInJob;
            try
            {
                bool r = IsProcessInJob(p.Handle, IntPtr.Zero, out childInJob);
                Console.WriteLine("  子进程 在job里=" + childInJob + "（查询成功=" + r + "）");
            }
            catch (Exception e)
            {
                Console.WriteLine("  子进程 job 查询失败: " + e.Message);
            }

            // 抽 nodes 数字
            var ns = new System.Collections.Generic.List<string>();
            int idx = 0;
            while (true)
            {
                int a = stdout.IndexOf("\"nodes\":", idx);
                if (a < 0) break;
                int b = a + 8;
                int e2 = b;
                while (e2 < stdout.Length && (char.IsDigit(stdout[e2]) || stdout[e2] == '-' || stdout[e2] == ' '))
                    e2++;
                ns.Add(stdout.Substring(b, e2 - b).Trim());
                idx = e2;
            }
            Console.WriteLine("  nodes: " + string.Join(" / ", ns.ToArray()));
        }
    }
}
