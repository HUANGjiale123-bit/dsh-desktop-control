/*
 * dsc-hookdiag —— 最小、自包含的低层键盘钩子诊断
 *
 * 由用户（对 Codex 取证的那位）指出的实验设计，按他的五步实现。
 * 目的：在一次运行里同时回答三个此前混在一起、因而无法区分的问题：
 *
 *   Q1 桌面是不是同一个对象？
 *      名字相同不代表对象相同。用 CompareObjectHandles 判定句柄身份——
 *      这是唯一正确的手段（Codex 的 Swift 版也为此导入了它）。
 *
 *   Q2 装钩的线程在不在泵消息？
 *      此前只证明了"某个线程在泵"（心跳来自另一个线程），没证明是装钩那个。
 *      本版打印装钩线程 id，并在循环里核对。
 *
 *   Q3 输入是合成的还是物理的？
 *      回调里**同时**维护两个计数：原始回调数 与 过滤掉 LLKHF_INJECTED 后的数。
 *      若原始 > 0 而过滤 = 0，则钩子本身完好，问题只在"测的是合成输入"。
 *
 * 关键实现纪律（照 Codex 的做法）：
 *   · 顺序必须是 OpenInputDesktop → SetThreadDesktop → 装钩 → 消息循环，
 *     全部在同一个线程上。SetThreadDesktop 对"已建过窗口或装过钩子"的线程会失败，
 *     所以顺序颠倒会静默无效。
 *   · 消息循环用 MsgWaitForMultipleObjects(1, &stopEvent, QS_ALLINPUT, INFINITE)
 *     + PeekMessage(&msg, NULL, 0, 0, PM_REMOVE)。**PeekMessage 的 hwnd 必须是 NULL**——
 *     指定了窗口就永远取不到线程消息，钩子回调正是在线程消息里被投递的。
 *   · 线程提优先级（THREAD_PRIORITY_TIME_CRITICAL），避免动画/IO 把钩子线程饿死
 *     导致 Windows 按 LowLevelHooksTimeout 静默摘钩。
 *   · dwThreadId 传 0（全局钩子）。传非 0 会变成线程局部钩子，恒为 0 次回调。
 *
 * 用法：dsc-hookdiag [秒数]
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class HookDiag
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
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr h);
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
    static extern bool SetThreadPriority(IntPtr hThread, int priority);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attr, bool manual, bool initial, IntPtr name);

    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms, uint wakeMask);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
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

    const int WH_KEYBOARD_LL = 13;
    const int HC_ACTION = 0;
    const int UOI_NAME = 2;
    const uint DESKTOP_READOBJECTS = 0x0001;
    const uint DESKTOP_WRITEOBJECTS = 0x0080;
    const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    const uint QS_ALLINPUT = 0x04FF;
    const uint PM_REMOVE = 0x0001;
    const uint WAIT_OBJECT_0 = 0;
    const uint WAIT_TIMEOUT = 258;
    const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    const uint LLKHF_EXTENDED = 0x01, LLKHF_LOWER_IL_INJECTED = 0x02, LLKHF_INJECTED = 0x10;

    static HookProc _proc;
    static IntPtr _hook = IntPtr.Zero;
    static string _log;

    // ★ 两个计数：原始 与 过滤后。区分"钩子坏了"与"输入是合成的"。
    static int _rawCallbacks;
    static int _realCallbacks;
    static int _logged;

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
        if (GetUserObjectInformationW(h, UOI_NAME, sb, (uint)sb.Capacity * 2, out need)) return sb.ToString();
        return "(err=" + Marshal.GetLastWin32Error() + ")";
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

    static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _rawCallbacks++;                      // 无条件
        try
        {
            if (nCode == HC_ACTION && lParam != IntPtr.Zero)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool injected = (k.flags & LLKHF_INJECTED) != 0;
                if (!injected) _realCallbacks++;

                if (_logged < 50)
                {
                    _logged++;
                    StringBuilder f = new StringBuilder();
                    if ((k.flags & LLKHF_EXTENDED) != 0) f.Append("EXTENDED ");
                    if ((k.flags & LLKHF_LOWER_IL_INJECTED) != 0) f.Append("LOWER_IL_INJECTED ");
                    if (injected) f.Append("INJECTED ");
                    if (f.Length == 0) f.Append("(none)");
                    L("回调 原始#" + _rawCallbacks + " 真实#" + _realCallbacks +
                      "  vk=0x" + k.vkCode.ToString("X2") + "(" + KeyName(k.vkCode) + ")" +
                      " scan=0x" + k.scanCode.ToString("X2") +
                      " flags=0x" + k.flags.ToString("X2") + "[" + f.ToString().Trim() + "]");
                }
            }
        }
        catch (Exception e) { L("回调内异常: " + e.GetType().Name + ": " + e.Message); }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e)
        {
            // 兜底：上一轮就是在这里哑崩的（连异常都打不出来），
            // 于是"程序早死"被误读成"钩子收不到输入"。任何异常都必须留下痕迹。
            try
            {
                Console.Error.WriteLine("致命异常: " + e.GetType().FullName);
                Console.Error.WriteLine("  message: " + e.Message);
                Console.Error.WriteLine("  stack: " + (e.StackTrace ?? "(none)"));
                File.AppendAllText(@"D:\dsh workspace\dsh-desktop-control\.state\hookdiag.log",
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] 致命异常 " +
                    e.GetType().FullName + ": " + e.Message + Environment.NewLine +
                    (e.StackTrace ?? "") + Environment.NewLine);
            }
            catch { }
            return 99;
        }
    }

    static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 40;
        _log = args.Length > 1 ? args[1] : @"D:\dsh workspace\dsh-desktop-control\.state\hookdiag.log";
        try { if (File.Exists(_log)) File.Delete(_log); } catch { }

        uint mainTid = GetCurrentThreadId();
        Console.WriteLine("=== 低层键盘钩子诊断 ===");
        L("进程 pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + "  主线程 tid=" + mainTid);
        L("前台窗口 hwnd=" + GetForegroundWindow());

        /* ── Q1：桌面句柄是否同一对象 ──
         * 名字相同不代表对象相同，必须用 CompareObjectHandles 判定。 */
        IntPtr inputDesk = OpenInputDesktop(0, false,
            DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP);
        if (inputDesk == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            L("OpenInputDesktop 失败 err=" + err + ": " + new System.ComponentModel.Win32Exception(err).Message);
        }
        IntPtr threadDesk = GetThreadDesktop(mainTid);
        L("Q1 桌面身份：");
        L("   OpenInputDesktop  = " + inputDesk + "  name=" + Name(inputDesk));
        L("   GetThreadDesktop  = " + threadDesk + "  name=" + Name(threadDesk));
        if (inputDesk != IntPtr.Zero && threadDesk != IntPtr.Zero)
        {
            /* 本机 kernel32 没有 CompareObjectHandles 导出（Windows 11 26200），
             * 直接 P/Invoke 会抛 EntryPointNotFoundException 并让整个程序在装钩前就死掉。
             * 正是这个崩溃让上一轮"0 次回调"变成了无意义的读数。
             *
             * 退而用两条可用的判定：
             *   a) 句柄值是否相同（快，但相等才可信——不相等不能推出不同对象）
             *   b) 名字比较（弱判据，仅作参考）
             * 若日后需要严格判定，可动态从 api-ms-win-core-handle-l1-1-0.dll 解析。 */
            bool sameHandle = (inputDesk == threadDesk);
            string n1 = Name(inputDesk), n2 = Name(threadDesk);
            L("   句柄值相同? " + (sameHandle ? "是（可确认同一对象）" : "否（不能据此判定不同对象，句柄可不同而指向同一桌面）"));
            L("   名称比较  : input=\"" + n1 + "\"  thread=\"" + n2 + "\" → " +
              (string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase) ? "同名" : "不同名 ⚠️"));
            L("   （CompareObjectHandles 在本机不可用，已跳过——这是诊断工具的限制，不是结论）");

            // 顺序纪律：先切桌面，再装钩。装完钩子再切会静默失败。
            L("   尝试 SetThreadDesktop(输入桌面)，置于装钩之前 …");
            if (SetThreadDesktop(inputDesk)) L("   SetThreadDesktop 成功，本线程现在在输入桌面上");
            else L("   SetThreadDesktop 失败 err=" + Marshal.GetLastWin32Error() +
                   "（若本线程已建窗口或装过钩子，此调用必然失败）");
        }

        /* ── 提优先级：避免钩子线程被饿死而遭静默摘钩 ── */
        if (SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL))
            L("线程优先级已提到 TIME_CRITICAL");
        else
            L("SetThreadPriority 失败 err=" + Marshal.GetLastWin32Error() + "（不致命）");

        /* ── Q2：装钩线程 ── */
        uint hookTid = GetCurrentThreadId();
        _proc = Proc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(IntPtr.Zero), 0);
        if (_hook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            L("装钩失败 Win32=" + err + ": " + new System.ComponentModel.Win32Exception(err).Message);
            return 1;
        }
        L("Q2 装钩线程：");
        L("   装钩时 tid=" + hookTid + "  句柄=" + _hook + "  dwThreadId=0（全局钩子）");
        L("   消息循环将运行在同一线程上：" + GetCurrentThreadId());

        IntPtr stopEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        IntPtr[] handles = new IntPtr[] { stopEvent };

        Thread stopper = new Thread(delegate ()
        {
            Thread.Sleep(seconds * 1000);
            L("时间到，设置停止事件");
            // 用命名无关的 SetEvent：CreateEventW 已建
            SetEvent(stopEvent);
        });
        stopper.IsBackground = true;
        stopper.Start();

        Console.WriteLine();
        Console.WriteLine("════ 现在请操作 ════");
        Console.WriteLine("  第 1 步：用**物理键盘**按几个键（字母、ESC、空格）");
        Console.WriteLine("  第 2 步：（可选）让我用 SendInput 打几个键做对照");
        Console.WriteLine("时长 " + seconds + " 秒。原始回调数与真实回调数分开统计。");
        Console.WriteLine();

        /* ── Q2 续：消息循环 ──
         * MsgWaitForMultipleObjects + PeekMessage(hwnd=NULL)。
         * hwnd 传 NULL 是硬要求：线程消息（钩子回调在其中投递）只能这样取到。 */
        bool running = true;
        int lastBeat = 0;
        long t0 = Environment.TickCount;
        while (running)
        {
            uint r = MsgWaitForMultipleObjects(1, handles, false, 1000, QS_ALLINPUT);

            if (r == WAIT_OBJECT_0) { running = false; break; }   // 停止事件
            if (r == WAIT_TIMEOUT || r == WAIT_OBJECT_0 + 1)
            {
                MSG m;
                while (PeekMessageW(out m, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref m);
                    DispatchMessageW(ref m);
                }
            }

            int el = (int)((Environment.TickCount - t0) / 1000);
            if (el >= lastBeat + 5)
            {
                lastBeat = el;
                L("心跳 " + el + "s：原始回调 " + _rawCallbacks + " 次，真实（非注入）" + _realCallbacks + " 次" +
                  (_rawCallbacks == 0 ? "  ← 一次都没被调用" : ""));
            }
        }

        UnhookWindowsHookEx(_hook);
        if (inputDesk != IntPtr.Zero) CloseDesktop(inputDesk);

        L("=== 结束 ===");
        L("原始回调总计 : " + _rawCallbacks);
        L("真实回调总计 : " + _realCallbacks + "（已排除 LLKHF_INJECTED）");
        L("");
        if (_rawCallbacks == 0)
            L("判读：装钩线程在泵消息、dwThreadId=0、优先级已提，回调仍为 0 —— 输入事件没有投递给本进程。");
        else if (_realCallbacks == 0)
            L("判读：钩子工作正常，但收到的全是合成输入（LLKHF_INJECTED）。问题在测试输入，不在钩子。");
        else
            L("判读：钩子完好且收到真实输入 —— 之前的问题在集成方式（线程/泵/顺序）。");
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEvent(IntPtr hEvent);
}
