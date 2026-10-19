using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

class MiniTest
{
    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    [STAThread]
    static void Main(string[] args)
    {
        IntPtr target = new IntPtr(long.Parse(args[0]));
        var kids = new List<IntPtr>();
        var names = new List<string>();
        EnumChildWindows(target, delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            kids.Add(h);
            names.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);

        // 先发 WM_GETOBJECT（PowerShell 就是这么做的）
        foreach (IntPtr h in kids) SendMessage(h, 0x003D, IntPtr.Zero, new IntPtr(-4));
        System.Threading.Thread.Sleep(1000);

        // 只做这两件事：FromHandle + FindAll
        for (int i = 0; i < kids.Count; i++)
        {
            int n = -1;
            string err = "";
            try
            {
                var el = AutomationElement.FromHandle(kids[i]);
                if (el == null) { err = "null"; }
                else
                {
                    var found = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    n = found == null ? -1 : found.Count;
                }
            }
            catch (Exception e) { err = e.GetType().Name; }
            Console.WriteLine(names[i].PadRight(32) + " nodes=" + n + " " + err);
        }
    }
}
