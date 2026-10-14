using System;
using System.Runtime.InteropServices;

class CursorTruth
{
    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        SetProcessDPIAware();
        System.Threading.Thread.Sleep(150);

        POINT start;
        GetCursorPos(out start);
        Console.WriteLine("起始光标 = (" + start.X + "," + start.Y + ")");
        Console.WriteLine("前台窗口 = " + GetForegroundWindow());
        Console.WriteLine();

        // 连续 6 次，每次移动到不同位置，只看"光标实际有没有到"
        int[,] pts = { { 400, 300 }, { 900, 500 }, { 1400, 800 }, { 600, 200 }, { 1100, 900 }, { 300, 400 } };
        int arrived = 0;
        for (int i = 0; i < 6; i++)
        {
            int x = pts[i, 0], y = pts[i, 1];
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(200);
            POINT a;
            GetCursorPos(out a);
            bool hit = (a.X == x && a.Y == y);
            if (hit) arrived++;
            Console.WriteLine("  目标(" + x + "," + y + ") → 实际(" + a.X + "," + a.Y + ")  " +
                              (hit ? "✅ 到达" : "❌ 没到"));
        }

        Console.WriteLine();
        Console.WriteLine("到达 " + arrived + "/6");
        Console.WriteLine(arrived == 6 ? "结论：SetCursorPos 完全可用"
                        : arrived == 0 ? "结论：完全不可用"
                        : "结论：部分可用（间歇性！）");
    }
}
