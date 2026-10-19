/* ps_read —— 通过一个**由 dsc.exe 亲自启动的** PowerShell 子进程读 UIA 树。
 *
 * ── 为什么需要这个 ──
 *
 * 外部复核的原话：
 *   "第一步很具体：由插件实际使用的启动路径创建一个干净的 PowerShell 子进程，
 *    加载已验证 DLL，查询同一个已知内容控件。**必须先证明'插件的子进程也能读到'**，
 *    不能用另一个手工打开的 PowerShell 的成功代替。"
 *
 * 我手工从 bash 启动的 PowerShell 能读到 SuYou 的 29 个节点。
 * 但插件跑的是 `dsc.exe`，而 `dsc.exe` 是 DSH 的沙箱子进程 ——
 * 它的环境（token、作业对象、环境变量）和我手工那个不一样。
 *
 * 所以这个命令要做的只有一件事：**从 dsc.exe 里起 PowerShell，看能不能读到**。
 * 读到了，桥才有意义；读不到，说明桥这条路在插件里走不通，得回头查沙箱。
 *
 * ── 安全 ──
 *
 * 它执行 PowerShell 脚本，所以必须过和应用操作一样的闸门：
 * RequireNotHalted + RequireAllowedWindow，而且目标窗口必须在白名单里。
 * 脚本内容由本文件生成，不接收调用方传入的脚本片段。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Dsc
{
    internal static class PsRead
    {
        /// <summary>
        /// 在 dsc.exe 里起一个 PowerShell，读指定 hwnd 的 UIA 树。
        ///
        /// 脚本做三件事：
        ///   1. EnumChildWindows 枚举子窗口
        ///   2. 对每个子窗口 FromHandle + FindAll(Descendants)
        ///   3. 把「每个子窗口的节点数 + 前 N 个节点的 类型/名称/矩形」以 JSON 打到 stdout
        /// </summary>
        internal static Dictionary<string, object> Run(long hwndValue, List<long> childHwnds, int maxNodes, int timeoutMs)
        {
            string script = BuildScript(childHwnds, maxNodes);

            // ── 写到哪 ──
            //
            // 不能写 %TEMP%：dsc.exe 是沙箱子进程，只能写会话工作区。
            // 实测报的是「对路径 … 的访问被拒绝」—— 这个还好，至少说了权限。
            //
            // 优先用 DSC_PSREAD_DIR（插件会传工作区路径），
            // 退回到当前目录下的 .dsc-psread（工作区通常就是 cwd）。
            string dir = Environment.GetEnvironmentVariable("DSC_PSREAD_DIR");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Directory.GetCurrentDirectory(), ".dsc-psread");
            try { Directory.CreateDirectory(dir); } catch { }
            string tmp = Path.Combine(dir,
                "psread-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".ps1");
            File.WriteAllText(tmp, script, new UTF8Encoding(true));   // 带 BOM，PowerShell 5.1 才认 UTF-8

            var psi = new ProcessStartInfo();
            // 用绝对路径，避免 PATH 里被别的 powershell 抢
            string sysRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(sysRoot)) sysRoot = @"C:\Windows";
            psi.FileName = Path.Combine(sysRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe");
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + tmp + "\"";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();
            int exitCode = -1;
            bool timedOut = false;

            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) sbOut.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) sbErr.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        timedOut = true;
                        try { p.Kill(); } catch { }
                    }
                    else
                    {
                        exitCode = p.ExitCode;
                    }
                }
            }
            catch (Exception e)
            {
                sbErr.AppendLine("启动 PowerShell 失败: " + e.Message);
            }
            finally
            {
                if (Environment.GetEnvironmentVariable("DSC_PSREAD_KEEP") == null)
                    try { File.Delete(tmp); } catch { }
            }

            var result = new Dictionary<string, object>();
            result["hwnd"] = hwndValue;
            result["timedOut"] = timedOut;
            result["exitCode"] = exitCode;
            result["stdout"] = sbOut.ToString();
            result["stderr"] = sbErr.ToString();
            result["powershellPath"] = psi.FileName;
            return result;
        }

        /// <summary>
        /// 生成脚本。
        ///
        /// ---- 一个必须记住的教训 ----
        ///
        /// 第一版在脚本里用 Add-Type 定义 DllImport 来调 EnumChildWindows，
        /// 结果在插件里跑不动，报的是：
        ///
        ///     Add-Type : 对路径 "C:\Users\...\Temp\xxx.tmp" 的访问被拒绝
        ///
        /// 因为 dsc.exe 是沙箱子进程，**而它启动的 PowerShell 继承了同样的限制**，
        /// 写不了 %TEMP%，而 Add-Type 编译时正需要写那里。
        ///
        /// 所以现在改成：**子窗口句柄由 C# 那边枚举好，作为参数传进来**。
        /// 脚本只用 AutomationElement（现成 DLL，不需要编译），一个 Add-Type 都不调。
        /// </summary>
        private static string BuildScript(List<long> childHwnds, int maxNodes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("Add-Type -AssemblyName UIAutomationClient");
            sb.AppendLine("Add-Type -AssemblyName UIAutomationTypes");
            sb.AppendLine("");
            sb.Append("$hwnds = @(");
            for (int i = 0; i < childHwnds.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(childHwnds[i]);
            }
            sb.AppendLine(")");
            sb.AppendLine("");
            sb.AppendLine("$out = New-Object System.Collections.ArrayList");
            sb.AppendLine("foreach ($h in $hwnds) {");
            sb.AppendLine("  $rec = @{ hwnd = $h; nodes = -1; items = @() }");
            sb.AppendLine("  try {");
            sb.AppendLine("    $e = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$h)");
            sb.AppendLine("    if ($e -ne $null) {");
            sb.AppendLine("      $f = $e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)");
            sb.AppendLine("      if ($f -ne $null) {");
            sb.AppendLine("        $rec.nodes = $f.Count");
            sb.AppendLine("        $n = 0");
            sb.AppendLine("        foreach ($x in $f) {");
            sb.AppendLine("          if ($n -ge " + maxNodes + ") { break }");
            sb.AppendLine("          $ct = ''; try { $ct = $x.Current.ControlType.ProgrammaticName } catch {}");
            sb.AppendLine("          $nm = ''; try { $nm = $x.Current.Name } catch {}");
            sb.AppendLine("          $l=0;$t=0;$w=0;$hh=0");
            sb.AppendLine("          try { $b = $x.Current.BoundingRectangle; $l=[int]$b.Left; $t=[int]$b.Top; $w=[int]$b.Width; $hh=[int]$b.Height } catch {}");
            sb.AppendLine("          $rec.items += @{ type = $ct; name = $nm; l = $l; t = $t; w = $w; h = $hh }");
            sb.AppendLine("          $n++");
            sb.AppendLine("        }");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            sb.AppendLine("  } catch { $rec.nodes = -2 }");
            sb.AppendLine("  [void]$out.Add($rec)");
            sb.AppendLine("}");
            sb.AppendLine("$out | ConvertTo-Json -Depth 6 -Compress");
            return sb.ToString();
        }
    }
}
