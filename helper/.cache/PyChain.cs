using System;
using System.Diagnostics;
class PyChain {
    static void Main(string[] a) {
        var psi = new ProcessStartInfo();
        psi.FileName = a[0];   // python.exe
        psi.Arguments = "\"" + a[1] + "\" \"" + a[2] + "\"";  // pybridge.py script.ps1
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        using (var p = Process.Start(psi)) {
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Console.WriteLine("  " + (o.Length > 300 ? o.Substring(0, 300) : o).Replace("\n", " "));
            if (e.Length > 0) Console.WriteLine("  stderr: " + (e.Length > 200 ? e.Substring(0, 200) : e).Replace("\n", " "));
        }
    }
}
