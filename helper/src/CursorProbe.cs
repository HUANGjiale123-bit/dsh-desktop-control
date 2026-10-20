using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DscCursorProbe
{
    /// <summary>
    /// B1 方案的可行性探针：给 15 个标准光标各装一张"看不见的空白"，
    /// 每张在**不同像素**上带 alpha=1 的记号；然后把当前光标画成位图，
    /// 扫描那个记号，看能不能还原"现在用的是哪个槽位"。
    ///
    /// 为什么需要这一步：SetSystemCursor 之后所有光标长得一模一样，
    /// GetCursorInfo 的句柄也分不出来（实测句柄跨进程不可比）。
    /// 如果记号能被读回，"全透明 + 自动切形状"就成立；读不回就只剩
    /// "整套替换成 .cur"（48px）那条路。
    ///
    /// 用法：
    ///   dsc-cursor-probe.exe mark      装上带记号的空白（会真的让光标消失）
    ///   dsc-cursor-probe.exe read      读当前光标，打印识别到的槽位
    ///   dsc-cursor-probe.exe restore   恢复系统光标
    /// </summary>
    internal static class Program
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr hcur, int id);
        [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(int action, int param, IntPtr data, int winIni);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, int planes, int bitsPerPixel, byte[] bits);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon,
            int cx, int cy, int istep, IntPtr hbrFlickerFreeDraw, int flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        private const int SPI_SETCURSORS = 0x0057;
        private const int DI_NORMAL = 0x0003;

        /// <summary>15 个标准光标槽位（C# 5 没有元组，用两个平行数组）。</summary>
        private static readonly int[] SLOT_IDS =
        {
            32512, 32513, 32514, 32515, 32516,
            32642, 32643, 32644, 32645, 32646,
            32648, 32649, 32650, 32651, 32671,
        };

        private static readonly string[] SLOT_NAMES =
        {
            "ARROW", "IBEAM", "WAIT", "CROSS", "UPARROW",
            "SIZENWSE", "SIZENESW", "SIZEWE", "SIZENS",
            "SIZEALL", "NO", "HAND", "APPSTARTING", "HELP", "PIN",
        };

        /// <summary>第 i 个槽位的记号放在 (i*2, 0)：互不重叠，且都在光标位图内。</summary>
        private static Point MarkerFor(int index)
        {
            return new Point(index * 2, 0);
        }

        private static IntPtr MakeMarkedBlank(int markerX, int markerY, int size)
        {
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                // 几乎全透明，只在一个像素上留 alpha=1 的红点（人眼不可见）。
                // 记号：alpha 很低但**不是 0**，配一个"不透明"的掩码位，
                // 这样 GDI 不会把它当透明丢掉（全透明掩码会把 alpha 一并忽略）。
                bmp.SetPixel(markerX, markerY, Color.FromArgb(24, 255, 0, 0));
                IntPtr color = bmp.GetHbitmap(Color.FromArgb(0));
                byte[] maskBits = new byte[(size * size + 7) / 8];
                for (int i = 0; i < maskBits.Length; i++) maskBits[i] = 0xFF;
                // 1bpp 掩码按行自底向上、每行按字节对齐
                int rowBytes = (size + 7) / 8;
                int row = size - 1 - markerY;
                int bit = markerX;
                maskBits[row * rowBytes + (bit / 8)] &= (byte)~(0x80 >> (bit % 8));
                IntPtr mask = CreateBitmap(size, size, 1, 1, maskBits);
                ICONINFO info = new ICONINFO { fIcon = false, xHotspot = 0, yHotspot = 0, hbmMask = mask, hbmColor = color };
                return CreateIconIndirect(ref info);
            }
        }

        private static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0] : "read";

            if (cmd == "restore")
            {
                Console.WriteLine("restore=" + SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0));
                return 0;
            }

            if (cmd == "mark")
            {
                int ok = 0;
                for (int i = 0; i < SLOT_IDS.Length; i++)
                {
                    Point m = MarkerFor(i);
                    IntPtr h = MakeMarkedBlank(m.X, m.Y, 48);
                    if (h == IntPtr.Zero) continue;
                    if (SetSystemCursor(h, SLOT_IDS[i])) ok++;
                }
                Console.WriteLine("marked=" + ok + "/" + SLOT_IDS.Length);
                return 0;
            }

            if (cmd == "read")
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (!GetCursorInfo(ref ci) || ci.hCursor == IntPtr.Zero)
                {
                    Console.WriteLine("nohandle");
                    return 1;
                }

                // 把当前光标画进 32x32 位图，找 alpha>0 的像素。
                using (Bitmap bmp = new Bitmap(48, 48, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        IntPtr hdc = g.GetHdc();
                        try { DrawIconEx(hdc, 0, 0, ci.hCursor, 48, 48, 0, IntPtr.Zero, DI_NORMAL); }
                        finally { g.ReleaseHdc(hdc); }
                    }

                    int found = -1;
                    int maxAlpha = 0;
                    for (int y = 0; y < 48 && found < 0; y++)
                    {
                        for (int x = 0; x < 48; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            if (c.A > maxAlpha) maxAlpha = c.A;
                            if (c.A > 0)
                            {
                                // 记号在 (i*2, 0)
                                if (y == 0 && x % 2 == 0 && x / 2 < SLOT_NAMES.Length)
                                {
                                    found = x / 2;
                                    break;
                                }
                            }
                        }
                    }

                    if (found >= 0)
                    {
                        Console.WriteLine("slot=" + SLOT_NAMES[found] + " index=" + found);
                        return 0;
                    }

                    Console.WriteLine("unknown maxAlpha=" + maxAlpha + " (没读到记号)");
                    return 2;
                }
            }

            Console.WriteLine("用法: dsc-cursor-probe.exe mark|read|restore");
            return 2;
        }
    }
}
