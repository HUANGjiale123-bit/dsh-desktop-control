using System;
using System.Runtime.InteropServices;

class ForceFore
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]
    static extern IntPtr GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("用法: ForceFore <hwnd>"); return 1; }
        IntPtr target = new IntPtr(long.Parse(args[0]));

        Console.WriteLine("切换前前台 = " + GetForegroundWindow());

        if (IsIconic(target)) ShowWindow(target, 9);   // SW_RESTORE

        IntPtr fg = GetForegroundWindow();
        uint fgThread = (uint)GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint myThread = GetCurrentThreadId();

        // 附加到当前前台线程的输入队列，绕开"非前台进程不能抢焦点"的限制
        bool attached = false;
        if (fgThread != myThread)
            attached = AttachThreadInput(myThread, fgThread, true);

        BringWindowToTop(target);
        SetForegroundWindow(target);
        // 短暂置顶再取消，确保真的到最前
        SetWindowPos(target, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetWindowPos(target, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        if (attached) AttachThreadInput(myThread, fgThread, false);

        System.Threading.Thread.Sleep(250);
        IntPtr now = GetForegroundWindow();
        Console.WriteLine("切换后前台 = " + now + "   " + (now == target ? "✅ 成功" : "❌ 失败（目标 " + target + "）"));
        return now == target ? 0 : 2;
    }
}
