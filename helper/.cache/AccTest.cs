using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Accessibility;   // ← 关键：真的引用这个程序集

class AccTest
{
    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object acc);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [STAThread]
    static void Main(string[] args)
    {
        long target = args.Length > 0 ? long.Parse(args[0]) : 66988;
        var kids = new List<IntPtr>();
        var names = new List<string>();
        EnumChildWindows(new IntPtr(target), delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            kids.Add(h); names.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);

        Console.WriteLine("=== 先做一次 MSAA 调用，强制加载 Accessibility 程序集 ===");
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); // IID_IAccessible
        for (int i = 0; i < kids.Count; i++)
        {
            object acc = null;
            int hr = AccessibleObjectFromWindow(kids[i], 0xFFFFFFFC, ref iid, out acc);
            var ia = acc as IAccessible;
            string nm = "";
            try { if (ia != null) nm = ia.get_accName(0) ?? ""; } catch { }
            Console.WriteLine("  " + names[i].PadRight(30) + " hr=0x" + hr.ToString("X8") + " name='" + nm + "'");
            if (acc != null) Marshal.ReleaseComObject(acc);
        }

        Console.WriteLine("");
        Console.WriteLine("=== 然后再读 UIA ===");
        for (int i = 0; i < kids.Count; i++)
        {
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
