using System;
using System.Diagnostics;
class ModDump {
    static void Main() {
        var p = Process.GetCurrentProcess();
        foreach (ProcessModule m in p.Modules) Console.WriteLine(m.ModuleName);
    }
}
