// 对比两种父进程下 powershell.exe 的实际环境。
//
// ── 现在的位置 ──
//
//   bash / cmd / python 启动 → 读到 39
//   dsc.exe / DpiTest.exe / JobTest.exe（都是 .NET）启动 → 读到 5
//
// 已排除：DPI、-NonInteractive、CreateNoWindow、stdout 重定向、job object。
//
// 这一轮做的：让子进程把自己的环境（环境变量、模块、job、com apartment）
// 全打到文件里，两种父进程各跑一次，然后 diff。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class EnvCompare
{
    [DllImport("kernel32.dll")]
    static extern bool IsProcessInJob(IntPtr h, IntPtr job, out bool result);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    // 子进程跑这个：把环境 + 几个运行状态写成 JSON 行
    const string ProbeBody = @"
$psi = [System.Diagnostics.Process]::GetCurrentProcess()
$env = @{}
foreach ($k in [System.Environment]::GetEnvironmentVariables().Keys) {
    $env[$k] = [string][System.Environment]::GetEnvironmentVariable($k)
}
$mods = @()
foreach ($m in $psi.Modules) { $mods += $m.ModuleName }
$obj = @{
    pid = $psi.Id
    parent = ''
    env = $env
    modules = $mods
    apartment = $null
    sessionId = $psi.SessionId
}
$obj | ConvertTo-Json -Depth 5 | Out-File -FilePath $args[0] -Encoding UTF8
";

    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        string probeScript = Path.Combine(outDir, "env-probe.ps1");
        File.WriteAllText(probeScript, ProbeBody, new UTF8Encoding(true));

        bool inJob;
        IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob);
        Console.WriteLine("[本进程] 在 job 里 = " + inJob + "，.NET 版本 = " + Environment.Version);

        // 写自己的环境供对比
        var sb = new StringBuilder();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            sb.AppendLine(e.Key + "=" + e.Value);
        File.WriteAllText(Path.Combine(outDir, "env-parent.txt"), sb.ToString(), Encoding.UTF8);
        Console.WriteLine("[本进程] 环境变量 " + Environment.GetEnvironmentVariables().Count + " 个");

        string childOut = Path.Combine(outDir, "env-child.json");
        if (File.Exists(childOut)) File.Delete(childOut);

        var psi = new ProcessStartInfo();
        psi.FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + probeScript + "\" \"" + childOut + "\"";
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        using (var p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (o.Length > 0) Console.WriteLine("[子进程 stdout] " + o.Substring(0, Math.Min(300, o.Length)));
            if (err.Length > 0) Console.WriteLine("[子进程 stderr] " + err.Substring(0, Math.Min(400, err.Length)));

            bool childInJob;
            IsProcessInJob(p.Handle, IntPtr.Zero, out childInJob);
            Console.WriteLine("[子进程] 在 job 里 = " + childInJob + "，退出码 = " + p.ExitCode);
        }

        Console.WriteLine("[子进程输出文件存在] " + File.Exists(childOut));
    }
}
