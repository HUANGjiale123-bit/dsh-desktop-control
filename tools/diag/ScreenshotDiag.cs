using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// 分步诊断全屏抓取，找出 GDI+ 到底在哪一步失败。
//
// 背景：整屏抓取报"GDI+ 中发生一般性错误"，但不知道是
// 创建位图、BitBlt、FromHbitmap 还是 Save 出错。逐步加 try 定位。
class ScreenshotDiag
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int dx, int dy, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    const int SRCCOPY = 0x00CC0020;
    const int CAPTUREBLT = 0x40000000;

    static void Step(string name, Action a)
    {
        try { a(); Console.WriteLine("  OK   " + name); }
        catch (Exception e) { Console.WriteLine("  FAIL " + name + "  → " + e.GetType().Name + ": " + e.Message); }
    }

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : @"D:\scdiag.png";

        Console.WriteLine("=== 环境 ===");
        Step("SetProcessDPIAware",
            delegate { Console.WriteLine("       ret=" + SetProcessDPIAware()); });
        System.Threading.Thread.Sleep(150);
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        Console.WriteLine("  主屏: " + w + "x" + h);

        Console.WriteLine();
        Console.WriteLine("=== 目标路径可写性 ===");
        Step("写一个 1x1 的 PNG 到 " + outPath, delegate
        {
            using (Bitmap b = new Bitmap(1, 1))
            { b.SetPixel(0, 0, Color.Red); b.Save(outPath, ImageFormat.Png); }
        });
        Step("写一个 " + w + "x" + h + " 的内存位图（不 Save）", delegate
        {
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb)) { }
        });
        Step("写一个 " + w + "x" + h + " 的 PNG", delegate
        {
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.Blue);
                b.Save(outPath, ImageFormat.Png);
            }
        });

        Console.WriteLine();
        Console.WriteLine("=== GDI 抓屏链路 ===");
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            Console.WriteLine("  GetDC        → " + screenDc);
            memDc = CreateCompatibleDC(screenDc);
            Console.WriteLine("  CreateCompatibleDC → " + memDc);
            hBmp = CreateCompatibleBitmap(screenDc, w, h);
            Console.WriteLine("  CreateCompatibleBitmap → " + hBmp);
            old = SelectObject(memDc, hBmp);
            Console.WriteLine("  SelectObject → " + old);

            bool ok1 = BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY | CAPTUREBLT);
            Console.WriteLine("  BitBlt(SRCCOPY|CAPTUREBLT) → " + ok1 + "  err=" + Marshal.GetLastWin32Error());
            if (!ok1)
            {
                bool ok2 = BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY);
                Console.WriteLine("  BitBlt(SRCCOPY) → " + ok2 + "  err=" + Marshal.GetLastWin32Error());
            }

            Step("Image.FromHbitmap", delegate
            {
                using (Bitmap b = Image.FromHbitmap(hBmp))
                    Console.WriteLine("       得到 " + b.Width + "x" + b.Height + " fmt=" + b.PixelFormat);
            });

            Step("FromHbitmap → 转 24bpp → Save", delegate
            {
                using (Bitmap src = Image.FromHbitmap(hBmp))
                using (Bitmap copy = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(copy)) g.DrawImageUnscaled(src, 0, 0);
                    copy.Save(@"D:\scdiag-fromhbitmap.png", ImageFormat.Png);
                }
            });
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
        return 0;
    }
}
