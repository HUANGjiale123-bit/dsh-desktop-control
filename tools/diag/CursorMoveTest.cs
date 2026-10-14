using System;
using System.Runtime.InteropServices;

class CursorMoveTest
{
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
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    static string Cur()
    {
        POINT p; GetCursorPos(out p);
        return p.X + "," + p.Y;
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int tx = args.Length > 0 ? int.Parse(args[0]) : 733;
        int ty = args.Length > 1 ? int.Parse(args[1]) : 59;

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        Console.WriteLine("SM_CXSCREEN=" + sw + " SM_CYSCREEN=" + sh +
                          "  VIRTUAL=" + vw + "x" + vh);
        Console.WriteLine("起始光标 = " + Cur() + "   目标 = (" + tx + "," + ty + ")");
        Console.WriteLine();

        // A: 原地不动（诊断 API 本身是否被接受）
        bool a0 = SetCursorPos(Cur().Split(',')[0].Length > 0 ? int.Parse(Cur().Split(',')[0]) : 0,
                               int.Parse(Cur().Split(',')[1]));
        Console.WriteLine("[A] SetCursorPos(当前位置) = " + a0);
        System.Threading.Thread.Sleep(100);
        Console.WriteLine("    光标 = " + Cur());

        // B: 相对移动 +40,+40
        POINT b; GetCursorPos(out b);
        INPUT[] m1 = new INPUT[1];
        m1[0].type = 0; m1[0].mi.dx = 40; m1[0].mi.dy = 40; m1[0].mi.dwFlags = MOUSEEVENTF_MOVE;
        uint n1 = SendInput(1, m1, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(200);
        POINT a1; GetCursorPos(out a1);
        Console.WriteLine("[B] SendInput 相对(+40,+40) 返回=" + n1 +
                          "  位移=(" + (a1.X - b.X) + "," + (a1.Y - b.Y) + ")");

        // C: 绝对模式，用 SM_CXSCREEN 归一化（不用 VIRTUALDESK）
        INPUT[] m2 = new INPUT[1];
        m2[0].type = 0;
        m2[0].mi.dx = (int)Math.Round(tx * 65535.0 / (sw > 0 ? sw - 1 : 1));
        m2[0].mi.dy = (int)Math.Round(ty * 65535.0 / (sh > 0 ? sh - 1 : 1));
        m2[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        uint n2 = SendInput(1, m2, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(250);
        POINT a2; GetCursorPos(out a2);
        Console.WriteLine("[C] SendInput 绝对(无VIRTUALDESK) 归一化=(" + m2[0].mi.dx + "," + m2[0].mi.dy +
                          ") 返回=" + n2 + "  实际=" + a2.X + "," + a2.Y);

        // D: 绝对模式 + VIRTUALDESK
        INPUT[] m3 = new INPUT[1];
        m3[0].type = 0;
        m3[0].mi.dx = (int)Math.Round(tx * 65535.0 / (vw > 0 ? vw - 1 : 1));
        m3[0].mi.dy = (int)Math.Round(ty * 65535.0 / (vh > 0 ? vh - 1 : 1));
        m3[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        uint n3 = SendInput(1, m3, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(250);
        POINT a3; GetCursorPos(out a3);
        Console.WriteLine("[D] SendInput 绝对(+VIRTUALDESK) 归一化=(" + m3[0].mi.dx + "," + m3[0].mi.dy +
                          ") 返回=" + n3 + "  实际=" + a3.X + "," + a3.Y);

        Console.WriteLine();
        Console.WriteLine("=== 判读 ===");
        Console.WriteLine("  A=True  且光标未动 → SetCursorPos 被接受（返回值不可信）");
        Console.WriteLine("  B/C/D 位移为 0     → 本进程的所有鼠标注入都无效");
        Console.WriteLine("  任一有位移         → 该方法可用，改用之");
        return 0;
    }
}
