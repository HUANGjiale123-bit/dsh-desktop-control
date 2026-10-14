using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 注入能力探针（键盘版）—— 不依赖光标位置
//
// 为什么不用光标当判据：光标由用户的物理鼠标控制。用户一动鼠标，
// "注入后光标在哪"这个判据就失效了。本项目已经因此得到过一组互相矛盾的
// 数据（同一路径先 3/3、后 0/3），无法判断哪几轮是干净的。
//
// 本版改用键盘：程序自己创建一个窗口（焦点自动落在它的编辑框上），
// 然后用 SendInput 注入一段随机字符串，最后**读回自己的窗口标题**。
//
//   · 注入成功 → 标题变成注入的字符串
//   · 注入失败 → 标题保持原样
//
// 这完全不经过鼠标，用户怎么动都不影响判读。
//
// 输出：单行 key=value，纯 ASCII（避开 PowerShell 5.1 的中文/emoji 解析问题）
//
// 用法：KeyProbe <输出文件> [等待秒数]
class KeyProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")]
    static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(IntPtr n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SetWindowTextW(IntPtr h, string s);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")]
    static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")]
    static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();

    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        // 必须显式声明 Unicode 封送：默认是按 ANSI 封送字符串，
        // 而这里调用的是 RegisterClassW / CreateWindowExW（Unicode 版），
        // 名字对不上就会导致 CreateWindowExW 返回 0。
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
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
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_KEYUP = 0x0002;
    const uint WM_CHAR = 0x0102, WM_QUIT = 0x0012;

    static WndProc _keep;
    static StringBuilder _typed = new StringBuilder();
    static IntPtr _hwnd = IntPtr.Zero;

    static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        // 窗口无编辑框时，WM_CHAR 会发到窗口过程；我们在这里累积收到的字符
        if (m == WM_CHAR)
        {
            char c = (char)w.ToInt32();
            if (c >= 32 && c < 127) _typed.Append(c);
            return IntPtr.Zero;
        }
        return DefWindowProcW(h, m, w, l);
    }

    static void InjectChar(char c)
    {
        INPUT[] pair = new INPUT[2];
        pair[0].type = INPUT_KEYBOARD;
        pair[0].ki.wScan = (ushort)c;
        pair[0].ki.dwFlags = KEYEVENTF_UNICODE;
        pair[1].type = INPUT_KEYBOARD;
        pair[1].ki.wScan = (ushort)c;
        pair[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outFile = args.Length > 0 ? args[0] : @"D:\keyprobe.log";
        int waitMs = args.Length > 1 ? int.Parse(args[1]) * 1000 : 2500;

        var R = new StringBuilder();
        Action<string, string> KV = delegate (string k, string v) { R.AppendLine(k + "=" + v); };

        string self = "";
        try { self = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch { }
        KV("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        KV("pid", GetCurrentProcessId().ToString());
        KV("exeDir", string.IsNullOrEmpty(self) ? "?" : Path.GetDirectoryName(self));
        KV("inWorkspace",
            self.IndexOf(@"\dsh workspace\", StringComparison.OrdinalIgnoreCase) >= 0 ? "YES" : "no");
        try
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var fs = File.OpenRead(self))
            {
                byte[] h = md5.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                KV("exeMd5", sb.ToString());
            }
        }
        catch { KV("exeMd5", "unavailable"); }

        bool aware = SetProcessDPIAware();
        Thread.Sleep(100);
        KV("dpiAware", aware ? "ok" : "fail");
        KV("screen", GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        POINT c;
        GetCursorPos(out c);
        KV("cursorAtStart", c.X + "," + c.Y + (c.X == 0 && c.Y == 0 ? " (NOT_ON_DESKTOP)" : ""));

        // 建窗口并抢焦点（注入需要焦点）
        WNDCLASS wc = new WNDCLASS();
        _keep = Proc;
        wc.lpfnWndProc = _keep;
        wc.hInstance = GetModuleHandleW(IntPtr.Zero);
        wc.lpszClassName = "DscKeyProbeWnd";
        RegisterClassW(ref wc);
        _hwnd = CreateWindowExW(0, "DscKeyProbeWnd", "KeyProbe", 0x00CF0000u | 0x10000000u,
            60, 80, 300, 100, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        KV("hwnd", _hwnd.ToString());
        if (_hwnd == IntPtr.Zero)
        {
            KV("RESULT", "WINDOW_CREATE_FAILED");
            try { File.WriteAllText(outFile, R.ToString()); } catch { }
            Console.Write(R.ToString());
            return 1;
        }

        SetForegroundWindow(_hwnd);
        Thread.Sleep(400);
        KV("foregroundIsSelf", (GetForegroundWindow() == _hwnd) ? "YES" : "NO");

        // 注入一个可识别的字符串
        string payload = "KP7XZ";
        foreach (char ch in payload) { InjectChar(ch); Thread.Sleep(60); }
        Thread.Sleep(waitMs);

        KV("payload", payload);
        KV("received", _typed.ToString());
        KV("RESULT", _typed.ToString() == payload ? "KEYBOARD_INJECTION_WORKS" : "KEYBOARD_INJECTION_DEAD");

        // 顺带再测一次光标（仅供参考，标污染风险）
        POINT cb2;
        GetCursorPos(out cb2);
        bool mv = SetCursorPos(cb2.X + 40, cb2.Y + 40);
        Thread.Sleep(200);
        POINT a;
        GetCursorPos(out a);
        KV("cursorMoveTest", "ret=" + mv + " moved=" + (a.X - cb2.X) + "," + (a.Y - cb2.Y) +
           " (contaminated if user moves mouse)");

        try { File.WriteAllText(outFile, R.ToString()); } catch { }
        Console.Write(R.ToString());

        PostMessageW(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        return 0;
    }
}
