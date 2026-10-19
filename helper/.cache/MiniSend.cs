using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

class MiniSend
{
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")]
    static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    static IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l) { return SendMessageW(h, m, w, l); }
    delegate bool EnumProc(IntPtr h, IntPtr l);

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
            kids.Add(h); names.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);

        // 先摸一次 UIA，确保它的 COM 服务已起来
        try {
            var warm = AutomationElement.RootElement;
            Console.WriteLine("UIA warm-up: " + (warm != null));
        } catch (Exception e) { Console.WriteLine("warm-up failed: " + e.Message); }

        for (int i = 0; i < kids.Count; i++)
        {
            Console.Write(names[i].PadRight(30) + " hwnd=" + kids[i].ToString() + " ");
            IntPtr r1 = SendMessage(kids[i], 0x003D, IntPtr.Zero, new IntPtr(-4));
            IntPtr r2 = SendMessage(kids[i], 0x003D, IntPtr.Zero, new IntPtr(-25));
            Console.WriteLine(names[i].PadRight(30) + " OBJID_CLIENT->" + r1 + "  UiaRoot->" + r2);
        }

        System.Threading.Thread.Sleep(1200);

        Console.WriteLine("");
        Console.WriteLine("AFTER:");
        for (int i = 0; i < kids.Count; i++)
        {
            if (names[i].IndexOf("RenderWidgetHost") < 0 && names[i].IndexOf("WRY") < 0) continue;
            int n = -1;
            try
            {
                var el = AutomationElement.FromHandle(kids[i]);
                if (el != null)
                {
                    var f = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    n = f == null ? -1 : f.Count;
                }
            }
            catch (Exception e) { Console.WriteLine("  " + names[i] + " EX " + e.GetType().Name); continue; }
            Console.WriteLine("  " + names[i].PadRight(30) + " nodes=" + n);
        }
    }
}
