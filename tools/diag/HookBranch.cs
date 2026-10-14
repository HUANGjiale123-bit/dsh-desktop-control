/*
 * HookBranch —— 分支定位：哪一步导致 SetWindowsHookExW 返回 ERROR_ACCESS_DENIED(5)
 *
 * 背景：dsc-hookdiag 在 SetThreadDesktop(输入桌面) 之后装钩失败，报 Win32=5。
 * 而此前几次测试里"句柄非零"其实是假象——那个程序在装钩之前就因
 * CompareObjectHandles 的 EntryPointNotFoundException 崩掉了，我读到的句柄是无意义的值。
 *
 * 本程序把两种顺序分开测，各用**独立进程**（因为 SetThreadDesktop 一旦生效就无法撤销，
 * 而且"已建窗口/装过钩子的线程不能再切桌面"，同进程内两种顺序互相污染）：
 *
 *   A) 不切桌面，直接装钩
 *   B) 先 SetThreadDesktop(输入桌面)，再装钩
 *
 * 同时把"输入桌面的实际访问权限"打出来，因为 OpenInputDesktop 的 access 参数
 * 决定拿到的是只读句柄还是可写句柄——SetThreadDesktop 需要 DESKTOP_* 权限。
 *
 * 用法：dsc-hookbranch A|B [秒数]
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class HookBranch
{
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(IntPtr name);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder buf, uint len, out uint needed);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetThreadPriority(IntPtr t, int p);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr a, bool manual, bool init, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEvent(IntPtr h);

    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] h, bool all, uint ms, uint mask);
    [DllImport("user32.dll")]
    static extern bool PeekMessageW(out MSG m, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG m);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    const int WH_KEYBOARD_LL = 13, HC_ACTION = 0, UOI_NAME = 2;
    const uint QS_ALLINPUT = 0x04FF, PM_REMOVE = 0x0001, WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258;
    const int THREAD_PRIORITY_TIME_CRITICAL = 15;
    const uint LLKHF_INJECTED = 0x10;

    // 桌面访问权限
    const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_CREATEWINDOW = 0x0002;
    const uint DESKTOP_CREATEMENU = 0x0004, DESKTOP_HOOKCONTROL = 0x0008;
    const uint DESKTOP_JOURNALPLAYBACK = 0x0020, DESKTOP_ENUMERATE = 0x0040;
    const uint DESKTOP_WRITEOBJECTS = 0x0080, DESKTOP_SWITCHDESKTOP = 0x0100;
    const uint DESKTOP_ALL = 0x01FF;
    const uint GENERIC_ALL = 0x10000000;

    static HookProc _proc;
    static IntPtr _hook = IntPtr.Zero;
    static string _log;
    static int _raw, _real;

    static void L(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s;
        Console.WriteLine(line);
        try { File.AppendAllText(_log, line + Environment.NewLine); } catch { }
    }

    static string Name(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        StringBuilder sb = new StringBuilder(512);
        uint need;
        return GetUserObjectInformationW(h, UOI_NAME, sb, (uint)sb.Capacity * 2, out need) ? sb.ToString() : "(err)";
    }

    static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _raw++;
        try
        {
            if (nCode == HC_ACTION && lParam != IntPtr.Zero)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool inj = (k.flags & LLKHF_INJECTED) != 0;
                if (!inj) _real++;
                L("回调 原始#" + _raw + " 真实#" + _real + " vk=0x" + k.vkCode.ToString("X2") +
                  " flags=0x" + k.flags.ToString("X2") + (inj ? " [注入]" : " [物理]"));
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e)
        {
            string m = "致命异常 " + e.GetType().FullName + ": " + e.Message;
            Console.Error.WriteLine(m);
            try { File.AppendAllText(_log, m + Environment.NewLine); } catch { }
            return 99;
        }
    }

    static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string branch = args.Length > 0 ? args[0].ToUpperInvariant() : "A";
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 25;
        _log = @"D:\dsh workspace\dsh-desktop-control\.state\hookbranch-" + branch + ".log";
        try { if (File.Exists(_log)) File.Delete(_log); } catch { }

        L("=== 分支 " + branch + " ===");
        L("pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " tid=" + GetCurrentThreadId());

        if (branch == "B")
        {
            // 先拿一个权限最全的输入桌面句柄
            IntPtr d = OpenInputDesktop(0, false, DESKTOP_ALL);
            int e1 = Marshal.GetLastWin32Error();
            L("OpenInputDesktop(DESKTOP_ALL=0x1FF) = " + d + (d == IntPtr.Zero ? " err=" + e1 : " name=" + Name(d)));
            if (d == IntPtr.Zero)
            {
                d = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP);
                L("  退回 OpenInputDesktop(0x181) = " + d);
            }
            if (d != IntPtr.Zero)
            {
                bool ok = SetThreadDesktop(d);
                L("SetThreadDesktop = " + ok + (ok ? "" : " err=" + Marshal.GetLastWin32Error()));
            }
            L("线程桌面现在 = " + Name(GetThreadDesktop(GetCurrentThreadId())));
        }
        else
        {
            L("（分支 A：不切桌面。当前线程桌面 = " + Name(GetThreadDesktop(GetCurrentThreadId())) + "）");
        }

        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);

        _proc = Proc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(IntPtr.Zero), 0);
        int err = Marshal.GetLastWin32Error();
        if (_hook == IntPtr.Zero)
        {
            L("★ 装钩失败 Win32=" + err + ": " + new System.ComponentModel.Win32Exception(err).Message +
              "   [分支 " + branch + "]");
            L("   → 结论：分支 " + branch + " 下 Windows 拒绝安装全局键盘钩子。");
            return 5;
        }
        L("装钩成功 handle=" + _hook + "   [分支 " + branch + "]");

        IntPtr stop = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        IntPtr[] handles = new IntPtr[] { stop };
        Thread st = new Thread(delegate () { Thread.Sleep(seconds * 1000); SetEvent(stop); });
        st.IsBackground = true; st.Start();

        Console.WriteLine("请按物理键（字母/ESC）。" + seconds + " 秒。");

        long t0 = Environment.TickCount;
        int lastBeat = 0;
        bool running = true;
        while (running)
        {
            uint r = MsgWaitForMultipleObjects(1, handles, false, 1000, QS_ALLINPUT);
            if (r == WAIT_OBJECT_0) { running = false; break; }
            if (r == WAIT_TIMEOUT || r == WAIT_OBJECT_0 + 1)
            {
                MSG m;
                while (PeekMessageW(out m, IntPtr.Zero, 0, 0, PM_REMOVE))
                { TranslateMessage(ref m); DispatchMessageW(ref m); }
            }
            int el = (int)((Environment.TickCount - t0) / 1000);
            if (el >= lastBeat + 5)
            {
                lastBeat = el;
                L("心跳 " + el + "s：原始 " + _raw + " 真实 " + _real);
            }
        }

        UnhookWindowsHookEx(_hook);
        L("=== 分支 " + branch + " 结束：原始回调 " + _raw + "，真实 " + _real + " ===");
        return 0;
    }
}
