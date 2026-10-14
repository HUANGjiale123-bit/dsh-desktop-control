using System;
using System.Runtime.InteropServices;

class SendInputProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(uint t);
    [DllImport("user32.dll")]
    static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

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

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        uint tid = GetCurrentThreadId();
        Console.WriteLine("前台窗口   = " + GetForegroundWindow());
        Console.WriteLine("线程桌面   = " + GetThreadDesktop(tid));
        Console.WriteLine("输入桌面   = " + OpenInputDesktop(0, false, 0x1 | 0x80 | 0x100));
        Console.WriteLine();

        // 键盘：Unicode 'A'
        INPUT[] kb = new INPUT[2];
        kb[0].type = 1; kb[0].ki.wVk = 0; kb[0].ki.wScan = (ushort)'A'; kb[0].ki.dwFlags = 0x0004;
        kb[1].type = 1; kb[1].ki.wVk = 0; kb[1].ki.wScan = (ushort)'A'; kb[1].ki.dwFlags = 0x0004 | 0x0002;
        uint n1 = SendInput(2, kb, Marshal.SizeOf(typeof(INPUT)));
        Console.WriteLine("SendInput 键盘 Unicode('A') = " + n1 + "  err=" + Marshal.GetLastWin32Error());

        // 鼠标：相对移动 +30,+30
        POINT b; GetCursorPos(out b);
        INPUT[] ms = new INPUT[1];
        ms[0].type = 0;
        ms[0].mi.dx = 30; ms[0].mi.dy = 30;
        ms[0].mi.dwFlags = 0x0001;   // MOUSEEVENTF_MOVE
        uint n2 = SendInput(1, ms, Marshal.SizeOf(typeof(INPUT)));
        int e2 = Marshal.GetLastWin32Error();
        System.Threading.Thread.Sleep(200);
        POINT a; GetCursorPos(out a);
        Console.WriteLine("SendInput 鼠标 相对(+30,+30) = " + n2 + "  err=" + e2 +
                          "  实际位移=(" + (a.X - b.X) + "," + (a.Y - b.Y) + ")");

        Console.WriteLine();
        Console.WriteLine("判读：");
        Console.WriteLine("  n=0 表示 SendInput 被拒绝（看 err）");
        Console.WriteLine("  n=2/1 表示被接受，此时才该看 GetCursorPos 是否变化");
        return 0;
    }
}
