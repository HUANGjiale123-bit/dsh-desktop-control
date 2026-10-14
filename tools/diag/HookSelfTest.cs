/*
 * HookSelfTest —— 自注入对照：钩子能否收到本进程自己 SendInput 的按键
 *
 * 这个实验回答一个被混淆很久的问题：
 *   "钩子收不到按键" 到底是 (a) 钩子在本进程内完全不工作，
 *   还是 (b) 钩子工作正常，但用户物理按的键没有进入系统的钩子链？
 *
 * 做法：装好键盘钩子后，本进程用 SendInput 注入几次按键。注入的键会带上
 * LLKHF_INJECTED 标志，但仍会走一遍低层钩子链——所以：
 *
 *   自注入能被钩子收到  → 钩子链路完好，收不到的只是"物理输入"
 *                        （说明用户的按键没有进入本会话的钩子链，
 *                          与远程输入/合成输入/输入法路径有关）
 *   自注入也收不到      → 钩子在本进程内根本不工作，与输入来源无关
 *
 * 两种结果指向完全不同的下一步，所以必须分开测。
 *
 * 用法：dsc-hookselftest [秒数]
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class HookSelfTest
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
    static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);

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
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);

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

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    const int WH_KEYBOARD_LL = 13, HC_ACTION = 0;
    const uint QS_ALLINPUT = 0x04FF, PM_REMOVE = 0x0001, WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    const int THREAD_PRIORITY_TIME_CRITICAL = 15;
    const uint LLKHF_INJECTED = 0x10;

    static HookProc _proc;
    static IntPtr _hook = IntPtr.Zero;
    static string _log;
    static int _raw, _injected, _physical, _logged;

    static void L(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s;
        Console.WriteLine(line);
        try { File.AppendAllText(_log, line + Environment.NewLine); } catch { }
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
                if (inj) _injected++; else _physical++;
                if (_logged < 60)
                {
                    _logged++;
                    L("★回调 原始#" + _raw + " 注入#" + _injected + " 物理#" + _physical +
                      " vk=0x" + k.vkCode.ToString("X2") + " scan=0x" + k.scanCode.ToString("X2") +
                      " flags=0x" + k.flags.ToString("X2") + (inj ? " [注入]" : " [物理]"));
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>用 SendInput 注入一个 Unicode 字符。</summary>
    static bool InjectChar(char c)
    {
        INPUT[] pair = new INPUT[2];
        pair[0].type = INPUT_KEYBOARD;
        pair[0].ki.wScan = (ushort)c;
        pair[0].ki.dwFlags = KEYEVENTF_UNICODE;
        pair[1].type = INPUT_KEYBOARD;
        pair[1].ki.wScan = (ushort)c;
        pair[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        uint n = SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
        return n == 2;
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
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 30;
        _log = @"D:\dsh workspace\dsh-desktop-control\.state\hookselftest.log";
        try { if (File.Exists(_log)) File.Delete(_log); } catch { }

        L("=== 自注入对照 ===");
        L("pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " tid=" + GetCurrentThreadId());

        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);

        _proc = Proc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(IntPtr.Zero), 0);
        int err = Marshal.GetLastWin32Error();
        if (_hook == IntPtr.Zero)
        {
            L("装钩失败 err=" + err + " " + new System.ComponentModel.Win32Exception(err).Message);
            return 5;
        }
        L("键盘钩子已装 handle=" + _hook);

        IntPtr stop = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        IntPtr[] handles = new IntPtr[] { stop };
        Thread st = new Thread(delegate () { Thread.Sleep(seconds * 1000); SetEvent(stop); });
        st.IsBackground = true; st.Start();

        // 后台注入线程：先等钩子稳定，再注入几个字符
        Thread inj = new Thread(delegate ()
        {
            Thread.Sleep(1500);
            L("--- 开始自注入 4 个字符（SendInput + KEYEVENTF_UNICODE）---");
            string s = "Zx9Q";
            foreach (char c in s)
            {
                bool ok = InjectChar(c);
                L("   注入 '" + c + "' → " + (ok ? "SendInput 返回成功" : "SendInput 失败 err=" + Marshal.GetLastWin32Error()));
                Thread.Sleep(250);
            }
            L("--- 自注入结束。此后若你物理按键，也会记录。---");
            Thread.Sleep(2000);
            L("（自注入结束 2 秒后）原始回调 = " + _raw + "，其中注入 = " + _injected + "，物理 = " + _physical);
        });
        inj.IsBackground = true;
        inj.Start();

        Console.WriteLine("自动注入中；你也可以同时按物理键。共 " + seconds + " 秒。");

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
                L("心跳 " + el + "s：原始 " + _raw + " 注入 " + _injected + " 物理 " + _physical);
            }
        }

        UnhookWindowsHookEx(_hook);
        L("=== 结束：原始 " + _raw + "，注入 " + _injected + "，物理 " + _physical + " ===");
        L("");
        if (_raw == 0)
            L("判读：连本进程自己 SendInput 的按键都收不到 → 钩子在本进程内根本不工作，与输入来源无关。");
        else if (_injected > 0 && _physical == 0)
            L("判读：钩子链路完好（自注入收得到），但物理按键收不到 → 物理输入没有进入本会话的钩子链。");
        else
            L("判读：钩子能收到物理按键 —— 之前的问题在集成方式。");
        return 0;
    }
}
