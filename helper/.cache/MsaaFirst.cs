using System;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Accessibility;

class MsaaFirst {
    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object acc);
    [DllImport("oleacc.dll")]
    static extern int AccessibleChildren(IAccessible pacc, int start, int num,
        [Out] object[] children, out int obtained);

    [STAThread]
    static void Main(string[] a) {
        IntPtr h = new IntPtr(long.Parse(a[0]));
        bool msaa = a.Length > 1 && a[1] == "msaa";

        if (msaa) {
            Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            object acc;
            int hr = AccessibleObjectFromWindow(h, 0xFFFFFFFC, ref iid, out acc);
            Console.WriteLine("MSAA hr=0x" + hr.ToString("X8"));
            var ia = acc as IAccessible;
            if (ia != null) {
                Console.WriteLine("  accChildCount = " + ia.accChildCount);
                if (ia.accChildCount > 0) {
                    var kids = new object[ia.accChildCount];
                    int got;
                    int hr2 = AccessibleChildren(ia, 0, ia.accChildCount, kids, out got);
                    Console.WriteLine("  AccessibleChildren hr=0x" + hr2.ToString("X8") + " got=" + got);
                    int shown = 0;
                    foreach (var k in kids) {
                        if (shown >= 6) break;
                        string nm = ""; object rl = null;
                        try { nm = ia.get_accName(k); } catch { }
                        try { rl = ia.get_accRole(k); } catch { }
                        Console.WriteLine("    role=" + rl + " '" + nm + "'");
                        shown++;
                    }
                }
            }
        }

        Console.WriteLine();
        var el = AutomationElement.FromHandle(h);
        if (el == null) { Console.WriteLine("null"); return; }
        Console.WriteLine("IsControlElement = " + el.Current.IsControlElement);
        var all = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        Console.WriteLine("**Descendants = " + (all == null ? -1 : all.Count) + "**");
    }
}
