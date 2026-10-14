using System;
using System.Runtime.InteropServices;

class BlockInputTest
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool BlockInput(bool fBlockIt);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);

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
    const uint MOUSEEVENTF_MOVE = 0x0001;

    static string Cur() { POINT p; GetCursorPos(out p); return p.X + "," + p.Y; }

    static bool TryMove()
    {
        POINT b; GetCursorPos(out b);
        INPUT[] m = new INPUT[1];
        m[0].type = 0; m[0].mi.dx = 25; m[0].mi.dy = 25; m[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        uint n = SendInput(1, m, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(180);
        POINT a; GetCursorPos(out a);
        bool moved = (a.X != b.X || a.Y != b.Y);
        Console.WriteLine("    SendInput 返回=" + n + "  位移=(" + (a.X - b.X) + "," + (a.Y - b.Y) + ")" +
                          (moved ? "  ✅ 动了" : "  ❌ 没动"));
        return moved;
    }

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== BlockInput 检测 ===");
        Console.WriteLine("屏幕 = " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        Console.WriteLine("光标 = " + Cur());
        Console.WriteLine();

        Console.WriteLine("[1] 当前状态下的注入：");
        bool before = TryMove();

        Console.WriteLine();
        Console.WriteLine("[2] 尝试解除 BlockInput（调用 BlockInput(false)）：");
        bool unblocked = BlockInput(false);
        int err = unblocked ? 0 : Marshal.GetLastWin32Error();
        Console.WriteLine("    BlockInput(false) = " + unblocked +
                          (unblocked ? "" : "  err=" + err + " (" +
                           new System.ComponentModel.Win32Exception(err).Message + ")"));
        Console.WriteLine("    ※ 解除失败(err=5)说明本进程权限不足以解除，不代表没被 Block");

        System.Threading.Thread.Sleep(300);

        Console.WriteLine();
        Console.WriteLine("[3] 解除尝试后的注入：");
        bool after = TryMove();

        Console.WriteLine();
        Console.WriteLine("=== 判读 ===");
        if (before) Console.WriteLine("  注入本来就正常，BlockInput 无关。");
        else if (after) Console.WriteLine("  ✅ 调用 BlockInput(false) 后恢复 —— 之前确实被 BlockInput 压着。");
        else Console.WriteLine("  ❌ 解除后仍无效。BlockInput 不是原因（或本进程无权解除）。");
        return 0;
    }
}
