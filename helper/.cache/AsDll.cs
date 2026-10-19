using System;
using System.Collections.Generic;
using System.Windows.Automation;

public class ProbeDll
{
    public static List<string> Read(long hwndValue)
    {
        var outList = new List<string>();
        var el = AutomationElement.FromHandle(new IntPtr(hwndValue));
        if (el == null) { outList.Add("null"); return outList; }
        var found = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        outList.Add("count=" + (found == null ? -1 : found.Count));
        if (found != null)
        {
            int n = 0;
            foreach (AutomationElement e in found)
            {
                if (n >= 8) break;
                string ct = ""; try { ct = e.Current.ControlType.ProgrammaticName; } catch { }
                string nm = ""; try { nm = e.Current.Name; } catch { }
                outList.Add("  " + ct + " | " + nm);
                n++;
            }
        }
        return outList;
    }
}
