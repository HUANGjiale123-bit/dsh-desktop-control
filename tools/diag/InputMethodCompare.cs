/*
 * InputMethodCompare —— 对比两种光标移动方式在当前环境下是否可用
 *
 * 背景：SetCursorPos 在本环境报 Win32 错误 2（ERROR_FILE_NOT_FOUND），
 * 而 Codex 的 helper 用的是 SendInput 的绝对模式（不会碰 SetCursorPos）。
 * 需要实测哪个可用。
 *
 * 测试项：
 *   A. SetCursorPos(x, y)
 *   B. SendInput 绝对模式（MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK，归一化到 0..65535）
 *   C. SendInput 相对模式（MOUSEEVENTF_MOVE，dx/dy 为位移量）
 *
 * 每一项都回读真实光标位置，不用返回值当证据。
 *
 * 用法：dsc-inputtest [x] [y]
 */

using System;
using System.Runtime.InteropServices;
using System.Text;

class InputMethodCompare
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

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

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    static string Cur()
    {
        POINT p; GetCursorPos(out p);
        return p.X + "," + p.Y;
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int tx = args.Length > 0 ? int.Parse(args[0]) : 733;
        int ty = args.Length > 1 ? int.Parse(args[1]) : 59;

        Console.WriteLine("=== 光标注入方式对比 ===");
        Console.WriteLine("目标 = (" + tx + "," + ty + ")");
        Console.WriteLine("起始光标 = " + Cur());
        Console.WriteLine();

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        Console.WriteLine("虚拟桌面 = 原点(" + vx + "," + vy + ") 尺寸 " + vw + "x" + vh);
        Console.WriteLine();

        /* ── A: SetCursorPos ── */
        Console.WriteLine("[A] SetCursorPos(" + tx + "," + ty + ")");
        bool a = SetCursorPos(tx, ty);
        int ae = a ? 0 : Marshal.GetLastWin32Error();
        Console.WriteLine("    返回 = " + a + (a ? "" : "  err=" + ae + " (" +
            new System.ComponentModel.Win32Exception(ae).Message + ")"));
        Console.WriteLine("    实际光标 = " + Cur());
        Console.WriteLine();

        /* ── B: SendInput 绝对模式 ── */
        Console.WriteLine("[B] SendInput 绝对模式（归一化 0..65535 + VIRTUALDESK）");
        INPUT[] bi = new INPUT[1];
        bi[0].type = INPUT_MOUSE;
        bi[0].mi.dx = (int)Math.Round((tx - vx) * 65535.0 / (vw - 1));
        bi[0].mi.dy = (int)Math.Round((ty - vy) * 65535.0 / (vh - 1));
        bi[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        uint bn = SendInput(1, bi, Marshal.SizeOf(typeof(INPUT)));
        int be = bn == 0 ? Marshal.GetLastWin32Error() : 0;
        Console.WriteLine("    归一化值 = (" + bi[0].mi.dx + "," + bi[0].mi.dy + ")");
        Console.WriteLine("    返回 = " + bn + (bn == 0 ? "  err=" + be + " (" +
            new System.ComponentModel.Win32Exception(be).Message + ")" : ""));
        System.Threading.Thread.Sleep(120);
        Console.WriteLine("    实际光标 = " + Cur());
        Console.WriteLine();

        /* ── C: SendInput 相对模式 ── */
        Console.WriteLine("[C] SendInput 相对模式（先移到已知点，再相对位移）");
        POINT before; GetCursorPos(out before);
        // 先尝试把光标挪到一个已知位置：用绝对模式已试过，这里直接相对位移 +10,+10
        INPUT[] ci = new INPUT[1];
        ci[0].type = INPUT_MOUSE;
        ci[0].mi.dx = 10;
        ci[0].mi.dy = 10;
        ci[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        uint cn = SendInput(1, ci, Marshal.SizeOf(typeof(INPUT)));
        int ce = cn == 0 ? Marshal.GetLastWin32Error() : 0;
        System.Threading.Thread.Sleep(120);
        POINT after; GetCursorPos(out after);
        Console.WriteLine("    返回 = " + cn + (cn == 0 ? "  err=" + ce : ""));
        Console.WriteLine("    位移 = (" + (after.X - before.X) + "," + (after.Y - before.Y) + ")  期望 (10,10)");
        Console.WriteLine();

        Console.WriteLine("=== 判读 ===");
        if (a) Console.WriteLine("  SetCursorPos 可用。");
        else Console.WriteLine("  SetCursorPos 不可用（err=" + ae + "）。");
        if (bn == 1 && after.X != before.X) Console.WriteLine("  SendInput 可用 —— 应改用它（Codex 也正是用它）。");
        else if (bn == 1) Console.WriteLine("  SendInput 返回成功但光标未动。");
        else Console.WriteLine("  SendInput 被拒绝（err=" + be + "）。");
        return 0;
    }
}
