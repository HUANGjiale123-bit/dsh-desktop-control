/*
 * HookBoth —— 同时装键盘钩子与鼠标钩子，分开统计
 *
 * 为什么需要这个对照：此前所有测试只装了键盘钩子，收到 0 次回调。
 * 但"键盘钩子收不到"和"所有钩子都收不到"是**完全不同的结论**：
 *
 *   · 键盘 0 / 鼠标 >0  → 钩子机制本身正常，问题只在键盘事件上
 *                        （可能是输入法/远程输入/合成输入的路径绕开了键盘钩子）
 *   · 键盘 0 / 鼠标 0   → 本进程完全收不到任何钩子投递，与输入类型无关
 *
 * 两个钩子装在同一个线程、同一个消息循环上，变量只有一个：事件类型。
 *
 * 每个回调都无条件计数（raw），并单独统计排除 LLKHF_INJECTED 后的数量（real），
 * 这样"钩子坏了"与"输入是合成的"也能一并区分。
 *
 * 用法：dsc-hookboth [秒数]
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class HookBoth
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
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int ptX, ptY; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, HC_ACTION = 0;
    const uint QS_ALLINPUT = 0x04FF, PM_REMOVE = 0x0001, WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258;
    const int THREAD_PRIORITY_TIME_CRITICAL = 15;
    const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;

    static HookProc _kbProc, _msProc;
    static IntPtr _kbHook = IntPtr.Zero, _msHook = IntPtr.Zero;
    static string _log;

    static int _kbRaw, _kbReal, _msRaw, _msReal, _logged;

    static void L(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s;
        Console.WriteLine(line);
        try { File.AppendAllText(_log, line + Environment.NewLine); } catch { }
    }

    static string KeyName(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk == 0x1B) return "ESC";
        if (vk == 0x20) return "Space";
        if (vk == 0x0D) return "Enter";
        return "vk" + vk;
    }

    static IntPtr KbProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _kbRaw++;                                   // ★ 无条件
        try
        {
            if (nCode == HC_ACTION && lParam != IntPtr.Zero && _logged < 60)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool inj = (k.flags & LLKHF_INJECTED) != 0;
                if (!inj) _kbReal++;
                _logged++;
                L("★键盘回调 原始#" + _kbRaw + " 真实#" + _kbReal +
                  " vk=0x" + k.vkCode.ToString("X2") + "(" + KeyName(k.vkCode) + ")" +
                  " flags=0x" + k.flags.ToString("X2") + (inj ? " [注入]" : " [物理]"));
            }
        }
        catch { }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    static IntPtr MsProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _msRaw++;                                   // ★ 无条件
        try
        {
            if (nCode == HC_ACTION && lParam != IntPtr.Zero && _logged < 60)
            {
                MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                bool inj = (m.flags & LLMHF_INJECTED) != 0;
                if (!inj) _msReal++;
                _logged++;
                L("★鼠标回调 原始#" + _msRaw + " 真实#" + _msReal +
                  " msg=0x" + wParam.ToInt32().ToString("X4") +
                  " 位置=(" + m.ptX + "," + m.ptY + ")" +
                  " flags=0x" + m.flags.ToString("X2") + (inj ? " [注入]" : " [物理]"));
            }
        }
        catch { }
        return CallNextHookEx(_msHook, nCode, wParam, lParam);
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
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 40;
        _log = @"D:\dsh workspace\dsh-desktop-control\.state\hookboth.log";
        try { if (File.Exists(_log)) File.Delete(_log); } catch { }

        L("=== 键盘 + 鼠标钩子对照 ===");
        L("pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + "  tid=" + GetCurrentThreadId() +
          "  前台窗口=" + GetForegroundWindow());

        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);

        _kbProc = KbProc;
        _msProc = MsProc;

        _kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, GetModuleHandleW(IntPtr.Zero), 0);
        int kbErr = Marshal.GetLastWin32Error();
        L("键盘钩子: " + (_kbHook != IntPtr.Zero ? "装上 handle=" + _kbHook
              : "失败 err=" + kbErr + " " + new System.ComponentModel.Win32Exception(kbErr).Message));

        _msHook = SetWindowsHookExW(WH_MOUSE_LL, _msProc, GetModuleHandleW(IntPtr.Zero), 0);
        int msErr = Marshal.GetLastWin32Error();
        L("鼠标钩子: " + (_msHook != IntPtr.Zero ? "装上 handle=" + _msHook
              : "失败 err=" + msErr + " " + new System.ComponentModel.Win32Exception(msErr).Message));

        IntPtr stop = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        IntPtr[] handles = new IntPtr[] { stop };
        Thread st = new Thread(delegate () { Thread.Sleep(seconds * 1000); SetEvent(stop); });
        st.IsBackground = true; st.Start();

        Console.WriteLine();
        Console.WriteLine("════ 请依次操作 ════");
        Console.WriteLine("  1) 按几次键盘（字母/ESC/空格）");
        Console.WriteLine("  2) 移动并点击鼠标几次");
        Console.WriteLine("时长 " + seconds + " 秒。键盘与鼠标分开统计。");
        Console.WriteLine();

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
                L("心跳 " + el + "s：键盘 原始/真实 = " + _kbRaw + "/" + _kbReal +
                  "   鼠标 原始/真实 = " + _msRaw + "/" + _msReal);
            }
        }

        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);

        L("=== 结束 ===");
        L("键盘：原始 " + _kbRaw + "，真实 " + _kbReal);
        L("鼠标：原始 " + _msRaw + "，真实 " + _msReal);
        L("");
        if (_kbRaw == 0 && _msRaw == 0)
            L("判读：键盘与鼠标钩子都收不到任何事件 —— 本进程完全无法接收钩子投递（与输入类型无关）。");
        else if (_kbRaw == 0 && _msRaw > 0)
            L("判读：鼠标钩子正常、键盘钩子收不到 —— 钩子机制可用，问题只在键盘事件的投递路径上。");
        else
            L("判读：钩子能收到事件 —— 之前的问题在集成方式，不在环境。");
        return 0;
    }
}
