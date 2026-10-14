/*
 * RawInputTest —— 验证「Chromium 用 Raw Input、而合成输入不进 Raw Input 队列」这个假设
 *
 * 背景：
 *   实测键盘注入能进经典 Win32 窗口（WinForms 测试窗口、记事本），
 *   但进不了 Chromium 系应用（DSH 自己、腾讯视频 —— 都是 Electron）。
 *
 *   已知机制：Chromium 的键盘处理走 Raw Input（RegisterRawInputDevices）。
 *   而 Windows 对 SendInput 有一条规则：合成事件**不进入 Raw Input 队列**
 *   （否则按键记录器就能被自动化喂数据，这是安全设计）。
 *
 * 本程序同时做两件事：
 *   1. 注册 Raw Input 设备，统计能从 Raw Input 收到多少键盘事件
 *   2. 自己也用 SendInput 注入按键，看注入的事件出现在哪条路径上
 *
 * 判读：
 *   注入的键**没有**出现在 Raw Input 计数里 → 假设成立（这解释了 Electron 收不到）
 *   注入的键**出现**在 Raw Input 计数里 → 假设不成立，另有原因
 *
 * 用法：dsc-rawinput [秒数]
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class RawInputTest
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(IntPtr n);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG m);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG m);

    [DllImport("user32.dll")]
    static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RIM_TYPEKEYBOARD = 1;
    const uint WM_INPUT = 0x00FF;
    const uint WM_QUIT = 0x0012;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_KEYUP = 0x0002;
    const ushort HID_USAGE_PAGE_GENERIC = 0x01, HID_USAGE_GENERIC_KEYBOARD = 0x06;

    static WndProc _procKeepAlive;
    static string _log;
    static int _rawKeyboardCount;
    static int _windowMsgCount;      // 通过普通窗口消息收到的按键（WM_KEYDOWN）
    static IntPtr _hwnd;

    static void L(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s;
        Console.WriteLine(line);
        try { File.AppendAllText(_log, line + Environment.NewLine); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SetWindowTextW(IntPtr h, string s);

    static void UpdateTitle()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            SetWindowTextW(_hwnd, "RawInput=" + _rawKeyboardCount +
                                 "  窗口消息=" + _windowMsgCount +
                                 "   （物理按键应让两个数都涨；注入的键看涨哪个）");
        }
        catch { }
    }

    static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_INPUT)
        {
            uint size = 0;
            GetRawInputData(l, 0x10000003 /*RID_INPUT*/, IntPtr.Zero, ref size,
                (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            if (size > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(l, 0x10000003, buf, ref size,
                            (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) == size)
                    {
                        RAWINPUTHEADER hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                        if (hdr.dwType == RIM_TYPEKEYBOARD)
                        {
                            _rawKeyboardCount++;
                            UpdateTitle();
                            RAWKEYBOARD kb = (RAWKEYBOARD)Marshal.PtrToStructure(
                                new IntPtr(buf.ToInt64() + Marshal.SizeOf(typeof(RAWINPUTHEADER))),
                                typeof(RAWKEYBOARD));
                            if (_rawKeyboardCount <= 40)
                                L("★RawInput 键盘 #" + _rawKeyboardCount +
                                  " vk=0x" + kb.VKey.ToString("X2") + " msg=0x" + kb.Message.ToString("X4"));
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return IntPtr.Zero;
        }
        // 普通窗口消息路径
        if (m == 0x0100 || m == 0x0104)   // WM_KEYDOWN / WM_SYSKEYDOWN
        {
            _windowMsgCount++;
            UpdateTitle();
            if (_windowMsgCount <= 40)
                L("☆窗口消息 按键 #" + _windowMsgCount + " vk=0x" + w.ToInt32().ToString("X2"));
        }
        return DefWindowProcW(h, m, w, l);
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int secs = args.Length > 0 ? int.Parse(args[0]) : 20;
        _log = @"D:\dsh workspace\dsh-desktop-control\.state\rawinput.log";
        try { if (File.Exists(_log)) File.Delete(_log); } catch { }

        L("=== Raw Input 与合成输入的关系测试 ===");

        // 建窗口（Raw Input 需要 hwndTarget 或 INPUTSINK）
        WNDCLASS wc = new WNDCLASS();
        _procKeepAlive = Proc;
        wc.lpfnWndProc = _procKeepAlive;
        wc.hInstance = GetModuleHandleW(IntPtr.Zero);
        wc.lpszClassName = "DscRawInputTest";
        RegisterClassW(ref wc);

        // 用真正可见的顶层窗口。之前用 message-only（HWND_MESSAGE=-3）且 style=0，
        // CreateWindowExW 直接返回 0，导致后面 RegisterRawInputDevices 报参数错误(87)。
        // WS_OVERLAPPEDWINDOW = 0x00CF0000，WS_VISIBLE = 0x10000000
        _hwnd = CreateWindowExW(0, "DscRawInputTest", "RawInput 测试窗口（请看这里）",
            0x00CF0000u | 0x10000000u,
            100, 100, 520, 220, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        L("窗口 hwnd = " + _hwnd);
        if (_hwnd == IntPtr.Zero)
            L("★ 窗口创建失败 err=" + Marshal.GetLastWin32Error());

        RAWINPUTDEVICE[] dev = new RAWINPUTDEVICE[1];
        dev[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
        dev[0].usUsage = HID_USAGE_GENERIC_KEYBOARD;
        dev[0].dwFlags = RIDEV_INPUTSINK;      // 后台也能收
        dev[0].hwndTarget = _hwnd;

        bool reg = RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        int err = reg ? 0 : Marshal.GetLastWin32Error();
        L("RegisterRawInputDevices = " + reg + (reg ? "" : "  err=" + err +
            " (" + new System.ComponentModel.Win32Exception(err).Message + ")"));

        // 注入线程：延迟后注入几个键
        Thread inj = new Thread(delegate ()
        {
            Thread.Sleep(2000);
            L("--- 开始自我注入 4 个字符（SendInput + KEYEVENTF_UNICODE）---");
            foreach (char c in "Wm3K")
            {
                INPUT[] pair = new INPUT[2];
                pair[0].type = INPUT_KEYBOARD;
                pair[0].ki.wVk = 0; pair[0].ki.wScan = (ushort)c;
                pair[0].ki.dwFlags = KEYEVENTF_UNICODE;
                pair[1].type = INPUT_KEYBOARD;
                pair[1].ki.wVk = 0; pair[1].ki.wScan = (ushort)c;
                pair[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                uint n = SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
                L("   注入 '" + c + "' SendInput 返回 " + n);
                Thread.Sleep(300);
            }
            Thread.Sleep(1500);
            L("--- 注入结束后 ---");
            L("    RawInput 收到键盘事件 : " + _rawKeyboardCount);
            L("    窗口消息收到按键      : " + _windowMsgCount);
            UpdateTitle();
        });
        inj.IsBackground = true;
        inj.Start();

        L("现在请【用物理键盘随便按几个键】做对照，共 " + secs + " 秒。");

        Thread stopper = new Thread(delegate ()
        {
            Thread.Sleep(secs * 1000);
            PostMessageW(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        });
        stopper.IsBackground = true;
        stopper.Start();

        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        L("");
        L("=== 汇总 ===");
        L("RawInput 键盘事件总数 = " + _rawKeyboardCount);
        L("窗口消息按键总数     = " + _windowMsgCount);
        L("");
        if (_rawKeyboardCount == 0 && _windowMsgCount == 0)
            L("判读：连物理按键都没收到任何一路 —— 本进程收不到输入（与 Electron 无关）。");
        else if (_rawKeyboardCount == 0)
            L("判读：窗口消息收得到、RawInput 收不到 —— 与 Electron 的现象一致。");
        else
            L("判读：RawInput 正常。若注入的键没进 RawInput，则 Chromium 收不到合成输入的解释成立。");
        return 0;
    }
}
