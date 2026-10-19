using System;
using System.Diagnostics;
using System.Windows.Automation;
class ModDump2 {
    static void Main(string[] a) {
        // 真的用一下 UIA，确保相关模块被加载
        try {
            var el = AutomationElement.FromHandle(new IntPtr(long.Parse(a[0])));
            if (el != null) {
                var f = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Console.Error.WriteLine("nodes=" + (f == null ? -1 : f.Count));
            }
        } catch (Exception e) { Console.Error.WriteLine("EX " + e.GetType().Name); }
        var p = Process.GetCurrentProcess();
        foreach (ProcessModule m in p.Modules) Console.WriteLine(m.ModuleName);
    }
}
