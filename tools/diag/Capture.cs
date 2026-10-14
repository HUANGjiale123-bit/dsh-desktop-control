using System;
using System.IO;
using System.Runtime.InteropServices;

// 抓主屏到 BMP，**手写文件头**，完全不经过 GDI+。
//
// 为什么不用 Bitmap.Save：本机实测，从命令行 exe 里调 GDI+ 的 Save
// 一律失败（"GDI+ 中发生一般性错误"），连 1x1 的纯新建位图都失败；
// 而同一条路径下 PowerShell 的 Save 正常。既然定位不到差异，
// 就绕开它——GetDIBits 取原始像素，BMP 头是公开格式，手写即可。
//
// BMP 另有一个好处：无压缩、无编码器依赖，抓到的就是屏幕上的原始像素。
//
// 用法：Capture <输出.bmp> [--all]
class Capture
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr d);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr d);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr d, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr d, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr d);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER h; public uint c1, c2, c3; }

    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : @"D:\capture.bmp";
        bool all = args.Length > 1 && args[1] == "--all";

        SetProcessDPIAware();
        System.Threading.Thread.Sleep(150);

        int x, y, w, h;
        if (all)
        {
            x = GetSystemMetrics(SM_XVIRTUALSCREEN); y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            w = GetSystemMetrics(SM_CXVIRTUALSCREEN); h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }
        else { x = 0; y = 0; w = GetSystemMetrics(0); h = GetSystemMetrics(1); }

        IntPtr sd = IntPtr.Zero, md = IntPtr.Zero, bm = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            sd = GetDC(IntPtr.Zero);
            md = CreateCompatibleDC(sd);
            bm = CreateCompatibleBitmap(sd, w, h);
            old = SelectObject(md, bm);

            // CAPTUREBLT 把分层窗口一起拷进来
            if (!BitBlt(md, 0, 0, w, h, sd, x, y, SRCCOPY | CAPTUREBLT))
                BitBlt(md, 0, 0, w, h, sd, x, y, SRCCOPY);

            BITMAPINFO bi = new BITMAPINFO();
            bi.h.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.h.biWidth = w;
            bi.h.biHeight = -h;              // 负高度 = 自上而下，省一次翻转
            bi.h.biPlanes = 1;
            bi.h.biBitCount = 32;
            bi.h.biCompression = 0;          // BI_RGB

            int stride = w * 4;
            byte[] bits = new byte[stride * h];
            int got = GetDIBits(md, bm, 0, (uint)h, bits, ref bi, 0);
            if (got == 0) { Console.WriteLine("{\"ok\":false,\"error\":\"GetDIBits 失败\"}"); return 1; }

            // 手写 BMP：BITMAPFILEHEADER(14) + BITMAPINFOHEADER(40) + 像素
            int pixelBytes = stride * h;
            int fileSize = 14 + 40 + pixelBytes;
            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0x4D42);        // 'BM'
                bw.Write(fileSize);
                bw.Write((ushort)0); bw.Write((ushort)0);
                bw.Write(14 + 40);               // 像素数据偏移
                bw.Write(40);                    // DIB 头大小
                bw.Write(w);
                bw.Write(-h);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(0);                     // BI_RGB
                bw.Write(pixelBytes);
                bw.Write(2835); bw.Write(2835);
                bw.Write(0); bw.Write(0);
                bw.Write(bits);
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
            if (old != IntPtr.Zero) SelectObject(md, old);
            if (bm != IntPtr.Zero) DeleteObject(bm);
            if (md != IntPtr.Zero) DeleteDC(md);
            if (sd != IntPtr.Zero) ReleaseDC(IntPtr.Zero, sd);
        }
    }
}
