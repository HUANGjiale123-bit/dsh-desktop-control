// 把 wmtest.ps1 里能读到 38 个节点的那段逻辑，逐字搬成一个独立 exe。
//
// 目的：区分两件事 ——
//   ① 是 **powershell.exe 这个宿主**做了什么初始化，所以能读到
//   ② 还是 **Add-Type 编译出来的代码** 和我的 csc 编译出来的有差别
//
// 如果这个 exe 也能读到 38 个，说明 ① 不成立，问题在我原来的代码里。
// 如果读不到，说明是 ①，那就得去查 powershell.exe 启动时干了什么。

using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

class ExactCopy
{
    // 和 Add-Type 里完全相同的声明，一个字都不改
    public class W
    {
        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        public delegate bool EnumProc(IntPtr h, IntPtr l);
    }

    [DllImport("ole32.dll")]
    static extern int OleInitialize(IntPtr pvReserved);

    [STAThread]
    static void Main(string[] args)
    {
        // ── 假设：PowerShell 在启动时做过 OleInitialize ──
        //
        // [STAThread] 只调 CoInitializeEx(COINIT_APARTMENTTHREADED)。
        // OleInitialize 在此基础上额外初始化 OLE（剪贴板、拖放、COM 目录）。
        // 7 个假设都排除后，这是最可能的一个。
        try {
            int hr = OleInitialize(IntPtr.Zero);
            Console.WriteLine("OleInitialize hr=0x" + hr.ToString("X8"));
        } catch (Exception e) { Console.WriteLine("OleInitialize 失败: " + e.Message); }

        // 真正**用一下** System.Windows.Forms —— 加载它会连带初始化 OLE/COM。
        // 这正是 PowerShell 有、而我只引用 UIAutomationClient 时没有的一步。
        try {
            System.Windows.Forms.Application.EnableVisualStyles();
            Console.WriteLine("WinForms 初始化: ok");
        } catch (Exception e) { Console.WriteLine("WinForms 初始化失败: " + e.Message); }
        try {
            var f = new System.Windows.Forms.Form();
            Console.WriteLine("WinForms Form 创建: ok" + (f != null));
        } catch (Exception e) { Console.WriteLine("Form 创建失败: " + e.Message); }
        Console.WriteLine("");

        long targetHwnd = args.Length > 0 ? long.Parse(args[0]) : 66988;
        IntPtr target = new IntPtr(targetHwnd);

        var kids = new List<IntPtr>();
        var names = new List<string>();
        W.EnumProc cb = delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(256);
            W.GetClassNameW(h, sb, 256);
            kids.Add(h);
            names.Add(sb.ToString());
            return true;
        };
        W.EnumChildWindows(target, cb, IntPtr.Zero);

        Console.WriteLine("child windows:");
        for (int i = 0; i < kids.Count; i++)
            Console.WriteLine("  " + names[i] + " hwnd=" + kids[i].ToInt64());

        // 完全照脚本：先 UiaRootObjectId，再 OBJID_CLIENT
        Console.WriteLine("");
        Console.WriteLine("sending:");
        for (int i = 0; i < kids.Count; i++)
        {
            IntPtr a = W.SendMessage(kids[i], 0x003D, IntPtr.Zero, new IntPtr(-25));
            IntPtr b = W.SendMessage(kids[i], 0x003D, IntPtr.Zero, new IntPtr(-4));
            Console.WriteLine("  " + names[i].PadRight(30) + " UiaRoot=" + a + "  OBJID_CLIENT=" + b);
        }

        System.Threading.Thread.Sleep(1500);

        Console.WriteLine("");
        Console.WriteLine("UIA read:");
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
            catch (Exception e)
            {
                Console.WriteLine("  " + names[i].PadRight(30) + " EX " + e.GetType().Name);
                continue;
            }
            Console.WriteLine("  " + names[i].PadRight(30) + " nodes=" + n);
        }
    }
}
