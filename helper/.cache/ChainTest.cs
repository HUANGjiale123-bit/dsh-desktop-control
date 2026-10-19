// 关键实验：.NET 进程启动 cmd.exe，cmd.exe 再启动 PowerShell，还能读到 Chromium 吗？
//
// ── 为什么问这个 ──
//
//   父进程 bash / cmd / python（都非 .NET）→ 读到 39
//   父进程 .NET（dsc.exe / DpiTest.exe）  → 读到 5
//
// 如果中间夹一层 cmd.exe 就能隔断，那解法是"用 cmd 启动"；
// 如果隔不断，说明影响会沿着进程树传下去，那就必须换非 .NET 的桥。
//
// 三种启动方式，同一个脚本，比较 nodes 数字：
//   A. 直接启动 PowerShell            （已知：.NET 父进程下 → 5）
//   B. 经 cmd.exe /c 启动             （本实验的关键）
//   C. 经 start /b 再启动              （再套一层）

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

class ChainTest
{
    static int Main(string[] args)
    {
        string script = args[0];
        int totalPass = 0;

        // A. 直接
        totalPass += Run("A. 直接启动 powershell.exe",
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"");

        // B. 经 cmd /c
        totalPass += Run("B. 经 cmd.exe /c",
            @"C:\Windows\System32\cmd.exe",
            "/c \"\"" + @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" +
            "\" -NoProfile -ExecutionPolicy Bypass -File \\\"" + script + "\\\"\"");

        // C. 经 cmd /c start /b（再套一层，验证是不是层数问题）
        totalPass += Run("C. 经 cmd /c start /b /wait",
            @"C:\Windows\System32\cmd.exe",
            "/c start /b /wait \"\" " + @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" +
            " -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"");

        return totalPass;
    }

    static int Run(string label, string exe, string argv)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + label + " ===");
        var psi = new ProcessStartInfo();
        psi.FileName = exe;
        psi.Arguments = argv;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        try
        {
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                // 抽 nodes
                var ns = new System.Collections.Generic.List<string>();
                int idx = 0;
                while (true)
                {
                    int a = o.IndexOf("\"nodes\":", idx);
                    if (a < 0) break;
                    int b = a + 8, c = b;
                    while (c < o.Length && (char.IsDigit(o[c]) || o[c] == '-' || o[c] == ' ')) c++;
                    ns.Add(o.Substring(b, c - b).Trim());
                    idx = c;
                }
                if (ns.Count > 0)
                {
                    Console.WriteLine("  nodes: " + string.Join(" / ", ns.ToArray()));
                    // 第一个是顶层 Tauri Window（通常 6 或 5），最后一个是 RenderWidgetHost
                    string last = ns[ns.Count - 1];
                    int n;
                    if (int.TryParse(last, out n) && n > 10)
                    {
                        Console.WriteLine("  ** 读到了内容（最后一项 " + n + " > 10）**");
                        return 1;
                    }
                }
                else
                {
                    string firstErr = e.Length > 200 ? e.Substring(0, 200) : e;
                    Console.WriteLine("  没有 nodes。stdout " + o.Length + " 字节，stderr: " +
                        firstErr.Replace('\n', ' '));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  启动失败: " + ex.Message);
        }
        return 0;
    }
}
