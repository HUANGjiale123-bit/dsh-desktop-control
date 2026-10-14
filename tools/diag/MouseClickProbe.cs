using System;
using System.Runtime.InteropServices;

class MouseClickProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_WHEEL = 0x0800;

    static string Title(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(200);
        GetWindowTextW(h, sb, 200);
        return sb.ToString();
    }

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        SetProcessDPIAware();
        System.Threading.Thread.Sleep(100);

        POINT c;
        GetCursorPos(out c);
        Console.WriteLine("屏幕 = " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        Console.WriteLine("光标 = (" + c.X + "," + c.Y + ")");
        Console.WriteLine("光标处窗口 = " + WindowFromPoint(c) + " [" + Title(WindowFromPoint(c)) + "]");
        Console.WriteLine("前台窗口   = " + GetForegroundWindow() + " [" + Title(GetForegroundWindow()) + "]");
        Console.WriteLine();

        // 1) 点击（不移动，就在当前位置按左键）
        Console.WriteLine("--- [1] 在光标当前位置注入左键按下+抬起 ---");
        INPUT[] click = new INPUT[2];
        click[0].type = 0; click[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        click[1].type = 0; click[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
        uint n1 = SendInput(2, click, Marshal.SizeOf(typeof(INPUT)));
        Console.WriteLine("    SendInput 返回 = " + n1 + "  (期望 2)");
        Console.WriteLine("    ※ 若生效，前台窗口的某个按钮/焦点应当有变化");

        System.Threading.Thread.Sleep(600);
        Console.WriteLine("    点击后前台 = " + GetForegroundWindow() + " [" + Title(GetForegroundWindow()) + "]");

        // 2) 滚轮
        Console.WriteLine();
        Console.WriteLine("--- [2] 注入滚轮 ---");
        INPUT[] wheel = new INPUT[1];
        wheel[0].type = 0;
        wheel[0].mi.dwFlags = MOUSEEVENTF_WHEEL;
        wheel[0].mi.mouseData = unchecked((uint)(-120 * 3));   // 向下滚 3 格
        uint n2 = SendInput(1, wheel, Marshal.SizeOf(typeof(INPUT)));
        Console.WriteLine("    SendInput 返回 = " + n2 + "  (期望 1)");

        // 3) 键盘对照（已知可用）
        Console.WriteLine();
        Console.WriteLine("--- [3] 键盘对照（已知可用）---");
        INPUT[] kb = new INPUT[2];
        kb[0].type = 1; kb[0].ki.wScan = (ushort)'#';
        kb[0].ki.dwFlags = 0x0004;
        kb[1].type = 1; kb[1].ki.wScan = (ushort)'#';
        kb[1].ki.dwFlags = 0x0004 | 0x0002;
        uint n3 = SendInput(2, kb, Marshal.SizeOf(typeof(INPUT)));
        Console.WriteLine("    SendInput 返回 = " + n3 + "  (期望 2)");

        Console.WriteLine();
        Console.WriteLine("=== 判读 ===");
        Console.WriteLine("  三个都返回期望值 → SendInput 本身没被拒绝，鼠标类被静默丢弃");
        Console.WriteLine("  鼠标项返回 0    → SendInput 明确拒绝鼠标事件");
        return 0;
    }
}
