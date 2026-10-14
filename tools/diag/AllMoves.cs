using System;
using System.Runtime.InteropServices;

// 穷举所有光标移动方式，只信"光标实际位置"。
// 背景：SetCursorPos 报失败、SendInput 报成功但无效；
// 而 Codex 用同样的 API 能操作鼠标。所以把剩余的可能性一次测完。
class AllMoves
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll", SetLastError = true)]
    static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);

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

    const uint MOVE = 0x0001, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

    static string Cur() { POINT p; GetCursorPos(out p); return p.X + "," + p.Y; }

    static void Report(string label, int tx, int ty, string before)
    {
        POINT a; GetCursorPos(out a);
        bool hit = (a.X == tx && a.Y == ty);
        Console.WriteLine("  " + label.PadRight(38) + " 目标(" + tx + "," + ty + ") 实际(" + a.X + "," + a.Y + ")  " +
                          (hit ? "✅" : "❌"));
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        SetProcessDPIAware();
        System.Threading.Thread.Sleep(150);

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        Console.WriteLine("屏幕 " + sw + "x" + sh + "   虚拟桌面 " + vw + "x" + vh);
        Console.WriteLine("起始光标 " + Cur());
        Console.WriteLine();

        int tx = 700, ty = 500;

        // A: SetCursorPos
        SetCursorPos(tx, ty); System.Threading.Thread.Sleep(200);
        Report("A SetCursorPos", tx, ty, "");

        // B: mouse_event 相对（先记录，再相对移动一段）
        POINT b0; GetCursorPos(out b0);
        mouse_event(MOVE, 60, 40, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(200);
        POINT a0; GetCursorPos(out a0);
        Console.WriteLine("  " + "B mouse_event 相对(+60,+40)".PadRight(38) +
                          " 实际位移=(" + (a0.X - b0.X) + "," + (a0.Y - b0.Y) + ")  " +
                          ((a0.X != b0.X || a0.Y != b0.Y) ? "✅" : "❌"));

        // C: mouse_event 绝对（归一化到 65535）
        int nx = (int)Math.Round(tx * 65535.0 / (sw > 1 ? sw - 1 : 1));
        int ny = (int)Math.Round(ty * 65535.0 / (sh > 1 ? sh - 1 : 1));
        mouse_event(MOVE | ABSOLUTE, nx, ny, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(200);
        Report("C mouse_event 绝对", tx, ty, "");

        // D: SendInput 绝对 + VIRTUALDESK
        INPUT[] d = new INPUT[1];
        d[0].type = 0;
        d[0].mi.dx = (int)Math.Round(tx * 65535.0 / (vw > 1 ? vw - 1 : 1));
        d[0].mi.dy = (int)Math.Round(ty * 65535.0 / (vh > 1 ? vh - 1 : 1));
        d[0].mi.dwFlags = MOVE | ABSOLUTE | VIRTUALDESK;
        SendInput(1, d, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(200);
        Report("D SendInput 绝对+VIRTUALDESK", tx, ty, "");

        // E: SendInput 绝对无 VIRTUALDESK
        INPUT[] e = new INPUT[1];
        e[0].type = 0;
        e[0].mi.dx = nx; e[0].mi.dy = ny;
        e[0].mi.dwFlags = MOVE | ABSOLUTE;
        SendInput(1, e, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(200);
        Report("E SendInput 绝对（无 VIRTUALDESK）", tx, ty, "");

        Console.WriteLine();
        Console.WriteLine("最终光标 " + Cur());
        return 0;
    }
}
