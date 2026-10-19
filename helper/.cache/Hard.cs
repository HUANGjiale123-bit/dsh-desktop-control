using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Automation;

class Hard
{
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")]
    static extern IntPtr SM(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="GetClassNameW")]
    static extern int GCW(IntPtr h, StringBuilder s, int n);

    [STAThread]
    static void Main()
    {
        long[] hs = new long[] { 67064, 67072 };
        foreach (long hv in hs)
        {
            IntPtr h = new IntPtr(hv);
            var sb = new StringBuilder(256);
            GCW(h, sb, sb.Capacity);
            Console.WriteLine("hwnd=" + hv + " class=" + sb.ToString());
            IntPtr r1 = SM(h, 0x003D, IntPtr.Zero, new IntPtr(-4));
            Console.WriteLine("  SendMessageW(OBJID_CLIENT) = " + r1);
            IntPtr r2 = SM(h, 0x003D, IntPtr.Zero, new IntPtr(-25));
            Console.WriteLine("  SendMessageW(UiaRoot)     = " + r2);
        }
        Console.WriteLine("");
        Console.WriteLine("--- 然后读 UIA ---");
        foreach (long hv in hs)
        {
            IntPtr h = new IntPtr(hv);
            try
            {
                var el = AutomationElement.FromHandle(h);
                if (el == null) { Console.WriteLine("  " + hv + " null"); continue; }
                var f = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Console.WriteLine("  " + hv + " nodes=" + (f == null ? -1 : f.Count));
            }
            catch (Exception e) { Console.WriteLine("  " + hv + " EX " + e.GetType().Name); }
        }
    }
}
