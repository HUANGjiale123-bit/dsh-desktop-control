using System;
using System.Runtime.InteropServices;

class QuickCursor
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        SetProcessDPIAware();
        System.Threading.Thread.Sleep(100);

        Console.WriteLine("屏幕 = " + GetSystemMetrics(0) + "x" + GetSystemMetrics(1));

        POINT before;
        GetCursorPos(out before);
        Console.WriteLine("调用前光标 = " + before.X + "," + before.Y);

        // 测三个点
        int[,] pts = { { 400, 300 }, { 1200, 700 }, { 800, 500 } };
        for (int i = 0; i < 3; i++)
        {
            int x = pts[i, 0], y = pts[i, 1];
            bool ok = SetCursorPos(x, y);
            System.Threading.Thread.Sleep(250);
            POINT a;
            GetCursorPos(out a);
            Console.WriteLine("SetCursorPos(" + x + "," + y + ") 返回=" + ok +
                              "  实际=(" + a.X + "," + a.Y + ")" +
                              (a.X == x && a.Y == y ? "  ✅ 命中" : "  ❌ 未命中"));
        }

        // 如果全失败，再试相对模式
        POINT b2;
        GetCursorPos(out b2);
        Console.WriteLine();
        Console.WriteLine("光标稳定在 (" + b2.X + "," + b2.Y + ")，说明注入无效。");
        return 0;
    }
}
