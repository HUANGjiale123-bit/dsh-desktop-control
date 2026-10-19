using System;
using System.Reflection;

class LoadTest
{
    [STAThread]
    static void Main()
    {
        byte[] bytes = System.IO.File.ReadAllBytes(
            @"D:\dsh workspace\dsh-desktop-control\helper\.cache\ProbeDll.dll");
        Assembly asm = Assembly.Load(bytes);
        Type t = asm.GetType("ProbeDll");
        Console.WriteLine("DLL loaded: " + (t != null));

        object result = t.InvokeMember("Read",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod,
            null, null, new object[] { 67072L });
        var list = result as System.Collections.Generic.List<string>;
        if (list == null) { Console.WriteLine("返回值不对"); return; }
        Console.WriteLine("从内存加载的 DLL 读到的结果：");
        foreach (string s in list) Console.WriteLine("  " + s);
    }
}
