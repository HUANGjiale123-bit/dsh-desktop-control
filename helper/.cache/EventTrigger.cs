// 注册一个 UIA 事件处理器，触发 Chromium 的 UIA bridge。
//
// ── 这条思路从哪来 ──
//
// 已确认的事实：
//   PowerShell 纯 MSAA  → accChildCount = 1
//   PowerShell 的 UIA   → Descendants = 29，ProviderDescription 为空（真 provider）
//   我的 MSAA           → accChildCount = 0
//   我的 UIA            → Descendants = 0，ProviderDescription = "MSAA Proxy"
//
// 那 29 个节点不可能是 MSAA 来的（MSAA 只有 1 个），说明 Chromium 有一个
// **真正的 UIA provider**，而 PowerShell 触发了它。
//
// Chromium 的 UIA bridge 是条件启用的：只有它认为"有 UIA 客户端在监听"
// 才把无障碍树建起来。而客户端的"在监听"表现为**注册了事件处理器**。
//
// 所以这个程序做的唯一一件事：在调 FromHandle 之前注册一个事件处理器，
// 然后看 Descendants 会不会从 0 变成 29。

using System;
using System.Windows.Automation;

class EventTrigger
{
    [STAThread]
    static void Main(string[] args)
    {
        long hwndValue = args.Length > 0 ? long.Parse(args[0]) : 67072;
        bool register = args.Length > 1 && args[1] == "event";

        AutomationElement target = null;
        try
        {
            target = AutomationElement.FromHandle(new IntPtr(hwndValue));
        }
        catch (Exception e)
        {
            Console.WriteLine("FromHandle 异常: " + e.Message);
            return;
        }
        if (target == null) { Console.WriteLine("FromHandle = null"); return; }

        Console.WriteLine("注册前 Descendants = " + Count(target));

        if (!register)
        {
            Console.WriteLine("（不注册事件，对照组）");
            return;
        }

        // 注册一个结构变化处理器。Chromium 会因此认为有客户端在监听。
        //
        // 用 TreeScope.Subtree 覆盖整棵子树 —— Chromium 判断"有客户端"时
        // 看的正是有没有人订阅它那棵树的变更。
        AutomationEventHandler handler = delegate(object s, AutomationEventArgs e) { };
        try
        {
            Automation.AddStructureChangedEventHandler(
                target, TreeScope.Subtree, new StructureChangedEventHandler(
                    delegate(object s, StructureChangedEventArgs e) { }));
            Console.WriteLine("AddStructureChangedEventHandler(TreeScope.Subtree) 已注册");
        }
        catch (Exception e)
        {
            Console.WriteLine("注册结构变化事件失败: " + e.GetType().Name + ": " + e.Message);
        }

        try
        {
            Automation.AddAutomationEventHandler(
                AutomationElement.AutomationFocusChangedEvent,
                target, TreeScope.Subtree, handler);
            Console.WriteLine("AddAutomationEventHandler(FocusChanged) 已注册");
        }
        catch (Exception e)
        {
            Console.WriteLine("注册焦点事件失败: " + e.GetType().Name + ": " + e.Message);
        }

        // 给 Chromium 一点时间建树
        for (int i = 1; i <= 6; i++)
        {
            System.Threading.Thread.Sleep(400);
            // 每轮重新取根 —— 树建起来之后根可能换了
            AutomationElement again = null;
            try { again = AutomationElement.FromHandle(new IntPtr(hwndValue)); } catch { }
            if (again == null) continue;
            int n = Count(again);
            Console.WriteLine("  " + (i * 400) + "ms: Descendants = " + n);
            if (n > 5) break;
        }

        // 最后再读一次并列出几个，看是不是真内容
        try
        {
            var last = AutomationElement.FromHandle(new IntPtr(hwndValue));
            if (last != null)
            {
                var all = last.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                if (all != null && all.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("读到的节点（前 12 个）:");
                    int k = 0;
                    foreach (AutomationElement x in all)
                    {
                        if (k >= 12) break;
                        string ct = ""; try { ct = x.Current.ControlType.ProgrammaticName; } catch { }
                        string nm = ""; try { nm = x.Current.Name; } catch { }
                        // 托管 AutomationElement 没暴露 ProviderDescription，跳过这一项
                        Console.WriteLine("  [" + k + "] " + ct + " '" + nm + "'");

                        k++;
                    }
                }
            }
        }
        catch (Exception e) { Console.WriteLine("最后读取异常: " + e.Message); }
    }

    static int Count(AutomationElement el)
    {
        try
        {
            var all = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            return all == null ? -1 : all.Count;
        }
        catch { return -2; }
    }
}
