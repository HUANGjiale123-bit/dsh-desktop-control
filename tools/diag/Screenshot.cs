using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// 抓主屏全屏到 PNG，供人/模型查看。
//
// 为什么需要它：插件现有的 desktop_screenshot 是**按窗口**抓的（GDI 区域抓取，
// 靠窗口矩形），抓不到铺满屏幕的覆盖层。而指示层/逃生窗正好是这类东西。
//
// 抓法用屏幕 DC 的 BitBlt，不用 Graphics.CopyFromScreen ——
// 后者在本机实测报 GDI+ 一般性错误（CopyFromScreen 内部会做一次
// 与 DPI 虚拟化相关的处理，在 150% 缩放 + DPI aware 的组合下会失败）。
// BitBlt 直接对屏幕 DC 操作，绕开那一层。
//
// 用法：Screenshot <输出路径> [--all]
class Screenshot
{
    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern bool BitBlt(IntPtr dst, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    const int SRCCOPY = 0x00CC0020;
    const int CAPTUREBLT = 0x40000000;   // 把分层窗口也纳入抓取

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : @"D:\screenshot.png";
        bool all = args.Length > 1 && args[1] == "--all";

        SetProcessDPIAware();
        System.Threading.Thread.Sleep(150);

        int x, y, w, h;
        if (all)
        {
            x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }
        else { x = 0; y = 0; w = GetSystemMetrics(0); h = GetSystemMetrics(1); }

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new Exception("GetDC 失败");
            memDc = CreateCompatibleDC(screenDc);
            hBmp = CreateCompatibleBitmap(screenDc, w, h);
            old = SelectObject(memDc, hBmp);

            // CAPTUREBLT：没有这一位，某些分层窗口（含我们自己这种）不会被拷进来。
            // 这正是"指示层不出现在截图里"的一个已知原因。
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY);

            using (Bitmap bmp = Image.FromHbitmap(hBmp))
            {
                using (Bitmap copy = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(copy))
                        g.DrawImageUnscaled(bmp, 0, 0);
                    copy.Save(outPath, ImageFormat.Png);
                }
            }

            Console.WriteLine("{\"ok\":true,\"path\":\"" + outPath.Replace("\\", "\\\\") +
                              "\",\"origin\":[" + x + "," + y + "],\"size\":[" + w + "," + h + "]}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("{\"ok\":false,\"error\":\"" + e.Message.Replace("\"", "'") + "\"}");
            return 1;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
