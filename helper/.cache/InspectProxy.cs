// 查 MS.Internal.Automation.ProxyManager 的静态状态。
//
// ── 为什么查它 ──
//
// UIAutomationClient.dll 里有 HwndProxyElementProvider 和 ProxyManager。
// ProxyManager 有 ProxyScoping 嵌套类型 —— 说明代理的启用范围是可控的状态。
// 而 IsControlElement 正是客户端代理算出来的属性。
//
// 所以要看：两个进程里 ProxyManager 的静态字段是不是不一样。

using System;
using System.Reflection;

class InspectProxy
{
    static void Main(string[] args)
    {
        var asm = typeof(System.Windows.Automation.AutomationElement).Assembly;

        Type pm = asm.GetType("MS.Internal.Automation.ProxyManager");
        if (pm == null) { Console.WriteLine("找不到 ProxyManager"); return; }

        Console.WriteLine("=== ProxyManager ===");
        Console.WriteLine("  是静态类: " + (pm.IsAbstract && pm.IsSealed));

        Console.WriteLine();
        Console.WriteLine("  静态字段:");
        foreach (var f in pm.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object v = null;
            try { v = f.GetValue(null); } catch (Exception e) { v = "读取失败: " + e.GetType().Name; }
            string vs = v == null ? "null" : v.ToString();
            if (vs.Length > 120) vs = vs.Substring(0, 120) + "...";
            Console.WriteLine("    " + f.FieldType.Name + " " + f.Name + " = " + vs);
        }

        Console.WriteLine();
        Console.WriteLine("  静态方法:");
        foreach (var m in pm.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.IsSpecialName) continue;
            var ps = m.GetParameters();
            var sb = new System.Text.StringBuilder();
            foreach (var p in ps) sb.Append(p.ParameterType.Name + " " + p.Name + ", ");
            Console.WriteLine("    " + m.ReturnType.Name + " " + m.Name + "(" +
                sb.ToString().TrimEnd(' ', ',') + ")");
        }

        // ProxyScoping 枚举
        Console.WriteLine();
        Console.WriteLine("  ProxyScoping 枚举值:");
        Type scoping = pm.GetNestedType("ProxyScoping", BindingFlags.Public | BindingFlags.NonPublic);
        if (scoping != null && scoping.IsEnum)
        {
            foreach (var n in Enum.GetNames(scoping))
                Console.WriteLine("    " + n + " = " + Convert.ToInt32(Enum.Parse(scoping, n)));
        }
        else
        {
            Console.WriteLine("    （找不到）");
        }

        // HwndProxyElementProvider
        Console.WriteLine();
        Console.WriteLine("=== HwndProxyElementProvider ===");
        Type hp = asm.GetType("MS.Internal.Automation.HwndProxyElementProvider");
        if (hp != null)
        {
            Console.WriteLine("  基类: " + (hp.BaseType != null ? hp.BaseType.FullName : "?"));
            Console.WriteLine("  实现的接口: " +
                string.Join(", ", Array.ConvertAll(hp.GetInterfaces(), i => i.Name)));
        }

        // 触发一次 FromHandle，再看静态字段有没有变
        Console.WriteLine();
        Console.WriteLine("=== 调 FromHandle 之后再读静态字段 ===");
        try
        {
            long h = args.Length > 0 ? long.Parse(args[0]) : 67072;
            var el = System.Windows.Automation.AutomationElement.FromHandle(new IntPtr(h));
            if (el != null)
            {
                Console.WriteLine("  IsControlElement = " + el.Current.IsControlElement);
                var all = el.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    System.Windows.Automation.Condition.TrueCondition);
                Console.WriteLine("  Descendants = " + (all == null ? -1 : all.Count));
            }
        }
        catch (Exception e) { Console.WriteLine("  异常: " + e.Message); }

        Console.WriteLine("  静态字段（读之后）:");
        foreach (var f in pm.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object v = null;
            try { v = f.GetValue(null); } catch { }
            string vs = v == null ? "null" : v.ToString();
            if (vs.Length > 120) vs = vs.Substring(0, 120) + "...";
            Console.WriteLine("    " + f.Name + " = " + vs);
        }
    }
}
