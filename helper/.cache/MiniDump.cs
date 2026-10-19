using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

class MiniDump
{
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
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

        for (int i = 0; i < kids.Count; i++)
        {
            if (names[i].IndexOf("RenderWidgetHost") < 0) continue;
            Console.WriteLine("=== " + names[i] + " hwnd=" + kids[i] + " ===");
            try
            {
                var el = AutomationElement.FromHandle(kids[i]);
                if (el == null) { Console.WriteLine("  null"); continue; }
                int pid = 0; try { pid = el.Current.ProcessId; } catch { }
                string cls = ""; try { cls = el.Current.ClassName; } catch { }
                Console.WriteLine("  pid=" + pid + " class=" + cls);
                var found = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Console.WriteLine("  descendants=" + (found == null ? -1 : found.Count));
                if (found != null)
                {
                    int n = 0;
                    foreach (AutomationElement e in found)
                    {
                        if (n >= 12) break;
                        string ct = ""; try { ct = e.Current.ControlType.ProgrammaticName; } catch { }
                        string nm = ""; try { nm = e.Current.Name; } catch { }
                        Console.WriteLine("    [" + n + "] " + ct + " | " + nm);
                        n++;
                    }
                }
            }
            catch (Exception e) { Console.WriteLine("  EXCEPTION " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
