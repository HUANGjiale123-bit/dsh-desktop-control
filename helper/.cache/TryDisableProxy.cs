// 反射调用 ProxyManager.DisableDefaultProxies()，看能否恢复读取。
//
// ── 线索 ──
//
// UIAutomationClient.dll 里有 MS.Internal.Automation.ProxyManager：
//
//   static bool   _defaultProxiesNeeded = True
//   static string _defaultProxyAssembly = "UIAutomationClientsideProviders"
//   static void   LoadDefaultProxies()
//   static void   DisableDefaultProxies()
//
// 客户端代理参与计算 IsControlElement，而这个属性正是两个进程的差异所在。
// 所以：如果关掉默认代理，让 UIA 退回到原始 provider 语义，可能就能读到内容。
//
// 这不是"改别人的内部状态"的歪路 —— 它只是验证一个假设。如果成立，
// 正确做法是在文档里写明依赖了 UIAutomationClient 的这个内部行为，
// 并把它包在一个明确的开关里，而不是永远靠反射。

using System;
using System.Reflection;
using System.Windows.Automation;

class TryDisableProxy
{
    [STAThread]
    static void Main(string[] args)
    {
        long hwndValue = args.Length > 0 ? long.Parse(args[0]) : 67072;
        bool disable = args.Length > 1 && args[1] == "disable";

        var asm = typeof(AutomationElement).Assembly;
        Type pm = asm.GetType("MS.Internal.Automation.ProxyManager");
        if (pm == null) { Console.WriteLine("找不到 ProxyManager"); return; }

        if (disable)
        {
            var m = pm.GetMethod("DisableDefaultProxies",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) { Console.WriteLine("找不到 DisableDefaultProxies"); return; }
            try
            {
                m.Invoke(null, null);
                Console.WriteLine("DisableDefaultProxies() 已调用");
            }
            catch (Exception e)
            {
                Console.WriteLine("调用失败: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                return;
            }
            // 确认状态真的变了
            var f = pm.GetField("_defaultProxiesNeeded",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
                Console.WriteLine("  _defaultProxiesNeeded = " + f.GetValue(null));
        }
        else
        {
            Console.WriteLine("（不调用 DisableDefaultProxies，对照组）");
        }

        Console.WriteLine();
        try
        {
            var el = AutomationElement.FromHandle(new IntPtr(hwndValue));
            if (el == null) { Console.WriteLine("FromHandle = null"); return; }
            Console.WriteLine("IsControlElement = " + el.Current.IsControlElement);
            Console.WriteLine("IsContentElement = " + el.Current.IsContentElement);
            Console.WriteLine("Name = '" + el.Current.Name + "'");
            var all = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            Console.WriteLine("**Descendants = " + (all == null ? -1 : all.Count) + "**");

            if (all != null && all.Count > 0)
            {
                int n = 0;
                foreach (AutomationElement x in all)
                {
                    if (n >= 10) break;
                    string ct = ""; try { ct = x.Current.ControlType.ProgrammaticName; } catch { }
                    string nm = ""; try { nm = x.Current.Name; } catch { }
                    Console.WriteLine("  [" + n + "] " + ct + " '" + nm + "'");
                    n++;
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("异常: " + e.GetType().Name + ": " + e.Message);
        }
    }
}
