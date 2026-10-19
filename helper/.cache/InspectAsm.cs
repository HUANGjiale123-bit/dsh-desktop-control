// 查 AutomationElement 内部持有什么 —— 这能解释 PowerShell 为什么读得到。
//
// ── 问题 ──
//
//   托管 AutomationElement.FromHandle：
//     PowerShell 里 → 读到 29，IsControlElement=False
//     我的 .NET exe  → 读到 0， IsControlElement=True
//
//   原生 CUIAutomation.ElementFromHandle：
//     我的 .NET exe  → 读到 0（和托管一致）
//
// 所以 PowerShell 走的不是原生 COM 路径。那它走的是什么？
//
// 这个程序用反射把 AutomationElement 的私有字段、内部类型全打出来，
// 在同一份代码、两个不同的父进程下各跑一次，看结构有没有差别。

using System;
using System.Reflection;
using System.Text;

class InspectAsm
{
    static void Main(string[] args)
    {
        long hwndValue = args.Length > 0 ? long.Parse(args[0]) : 67072;

        var asmEl = typeof(System.Windows.Automation.AutomationElement).Assembly;
        Console.WriteLine("AutomationElement 程序集: " + asmEl.Location);
        Console.WriteLine("  版本: " + asmEl.FullName);
        Console.WriteLine();

        // 1. AutomationElement 的实例字段（看它内部挂了什么）
        Console.WriteLine("=== AutomationElement 的实例字段 ===");
        foreach (var f in typeof(System.Windows.Automation.AutomationElement)
                     .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            Console.WriteLine("  " + f.FieldType.Name + " " + f.Name);
        }
        Console.WriteLine();

        // 2. 程序集里所有类型（找有没有 MSAA/legacy 相关的实现）
        Console.WriteLine("=== 程序集里的类型（含 Accessible/MSAA/Legacy 的）===");
        Type[] types;
        try { types = asmEl.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        int shown = 0;
        foreach (var t in types)
        {
            if (t == null) continue;
            string n = t.FullName ?? "";
            if (n.IndexOf("Accessible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("MSAA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Legacy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine("  " + n);
                shown++;
                if (shown > 25) { Console.WriteLine("  ..."); break; }
            }
        }
        if (shown == 0) Console.WriteLine("  （没有）");
        Console.WriteLine();

        // 3. 实际读一次，看根元素内部挂的是什么对象
        Console.WriteLine("=== FromHandle 之后，根元素内部是什么 ===");
        try
        {
            var el = System.Windows.Automation.AutomationElement.FromHandle(new IntPtr(hwndValue));
            if (el == null)
            {
                Console.WriteLine("  null");
            }
            else
            {
                Console.WriteLine("  IsControlElement = " + el.Current.IsControlElement);
                Console.WriteLine("  IsContentElement = " + el.Current.IsContentElement);
                foreach (var f in typeof(System.Windows.Automation.AutomationElement)
                             .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    object v = null;
                    try { v = f.GetValue(el); } catch { }
                    Console.WriteLine("  字段 " + f.Name + " (" + f.FieldType.Name + ") = " +
                        (v == null ? "null" : v.GetType().FullName));
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("  FromHandle 异常: " + e.GetType().Name + ": " + e.Message);
        }
    }
}
