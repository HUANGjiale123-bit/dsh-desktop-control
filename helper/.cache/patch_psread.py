import io, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

p = 'src/PsRead.cs'
s = io.open(p, encoding='utf-8').read()

# ① Run 签名加 hwnd 列表
s = s.replace(
    'internal static Dictionary<string, object> Run(long hwndValue, int maxNodes, int timeoutMs)\n        {\n            string script = BuildScript(hwndValue, maxNodes);',
    'internal static Dictionary<string, object> Run(long hwndValue, List<long> childHwnds, int maxNodes, int timeoutMs)\n        {\n            string script = BuildScript(childHwnds, maxNodes);')

start = s.find('        private static string BuildScript(')
end = s.rfind('    }\n}')
assert start > 0 and end > start, 'find failed'

new_script = r'''        /// <summary>
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
'''

s = s[:start] + new_script + s[end:]
io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('  OK BuildScript')

p2 = 'src/Program.cs'
s2 = io.open(p2, encoding='utf-8').read()
s2 = s2.replace(
    'Dictionary<string, object> r = PsRead.Run(hwnd.ToInt64(), maxNodes, 25000);',
    'var kids = new List<long>();\n'
    '            kids.Add(hwnd.ToInt64());\n'
    '            var contentHwnds = Probe.FindContentWindows(hwnd);\n'
    '            foreach (IntPtr c in contentHwnds) kids.Add(c.ToInt64());\n\n'
    '            Dictionary<string, object> r = PsRead.Run(hwnd.ToInt64(), kids, maxNodes, 25000);')
io.open(p2, 'w', encoding='utf-8', newline='').write(s2)
print('  OK CmdPsRead')
