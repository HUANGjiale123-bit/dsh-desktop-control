using System;
using System.Runtime.InteropServices;

class ActivateWin
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")]
    static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    static extern IntPtr GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")]
    static extern IntPtr GetLastActivePopup(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    delegate bool EnumProc(IntPtr h, IntPtr p);

    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
    const int SW_RESTORE = 9, SW_SHOW = 5, SW_MINIMIZE = 6;

    const uint WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007, WM_NCACTIVATE = 0x0086;
    const uint WA_ACTIVE = 1;

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 1) { Console.WriteLine("用法: ActivateWin <hwnd> [要一起最小化的hwnd...]"); return 1; }
        IntPtr target = new IntPtr(long.Parse(args[0]));

        // 把其余指定窗口最小化（腾出前台位置）
        for (int i = 1; i < args.Length; i++)
        {
            IntPtr h = new IntPtr(long.Parse(args[i]));
            if (h == target) continue;
            if (!IsIconic(h))
            {
                ShowWindow(h, SW_MINIMIZE);
                Console.WriteLine("  已最小化 " + h);
            }
        }
        System.Threading.Thread.Sleep(500);

        Console.WriteLine("当前前台 = " + GetForegroundWindow() + "  目标 = " + target);

        if (IsIconic(target))
        {
            ShowWindow(target, SW_RESTORE);
            System.Threading.Thread.Sleep(300);
        }

        // 依次尝试多种手段
        SetWindowPos(target, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        System.Threading.Thread.Sleep(120);
        SetWindowPos(target, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(target);
        System.Threading.Thread.Sleep(200);

        if (GetForegroundWindow() != target)
        {
            // 用消息直接激活
            PostMessageW(target, WM_NCACTIVATE, new IntPtr(WA_ACTIVE), IntPtr.Zero);
            PostMessageW(target, WM_ACTIVATE, new IntPtr(WA_ACTIVE), IntPtr.Zero);
            SendMessageW(target, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
            System.Threading.Thread.Sleep(300);
        }

        if (GetForegroundWindow() != target)
        {
            // 再试一次置顶 + SetForegroundWindow 组合
            SetWindowPos(target, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(target);
            System.Threading.Thread.Sleep(250);
        }

        IntPtr now = GetForegroundWindow();
        Console.WriteLine("最终前台 = " + now + "   " + (now == target ? "✅ 成功" : "❌ 仍失败"));
        return now == target ? 0 : 2;
    }
}
