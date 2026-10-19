using System;
using System.Diagnostics;
class PyChain2 {
    // args: python.exe bridge.py script.ps1 [inherit]
    static void Main(string[] a) {
        bool inherit = a.Length > 3 && a[3] == "inherit";
        var psi = new ProcessStartInfo();
        psi.FileName = a[0];
        psi.Arguments = "\"" + a[1] + "\" \"" + a[2] + "\"";
        psi.UseShellExecute = false;
        // ★ 关键：不重定向时不继承任何句柄
        if (!inherit) {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        psi.CreateNoWindow = true;
        using (var p = Process.Start(psi)) {
            if (!inherit) {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Console.WriteLine("  " + (o.Length > 300 ? o.Substring(0, 300) : o).Replace("\n", " "));
            } else {
                p.WaitForExit();
                Console.WriteLine("  （子进程输出直接进了控制台）");
            }
        }
    }
}
