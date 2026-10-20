using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;

namespace DscFx
{
    /// <summary>
    /// 光标装饰探针（演示/评估用，不是产品逻辑）。
    ///
    /// 用法：
    ///   dsc-fx.exe &lt;显示尺寸&gt; &lt;总秒数&gt; &lt;png@anchorX,anchorY&gt; [更多...]
    ///
    /// 多个姿势按总时长平均轮换。
    ///
    /// ── 为什么是纯 Win32，不用 WinForms ──
    ///
    /// 实测 WinForms 版本的 Timer 在日志里一次都没触发，装饰只画了初始化那一帧。
    /// 换成 Threading.Timer + BeginInvoke 也没解决。分层窗口的更新本来就不需要消息循环，
    /// 所以这里建个 STATIC 分层窗口 + 主线程自己跑循环，行为只取决于这个 while。
    ///
    /// ── 锚点是什么 ──
    ///
    /// anchor 是"光标尖落在这张图的哪个像素上"。anchor 越靠左上、角色越偏右下；
    /// 想让角色**罩住光标**，anchor 要取到内容中间。
    /// </summary>
    internal static class Program
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int CX; public int CY; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
            int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey,
            ref BLENDFUNCTION pblend, int dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr hcur, int id);
        [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(int action, int param, IntPtr data, int winIni);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, int affinity);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, int planes, int bits, byte[] bitsPtr);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_NOACTIVATE = 0x8000000;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_TOPMOST = 0x8;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_HIDE = 0;
        private const int ULW_ALPHA = 2;
        private const byte AC_SRC_ALPHA = 1;
        private const int OCR_NORMAL = 32512;
        private const int SPI_SETCURSORS = 0x0057;
        private const int WDA_EXCLUDEFROMCAPTURE = 0x11;

        private sealed class Pose
        {
            public Bitmap Sprite;
            public IntPtr HBitmap;
            public int AnchorX;
            public int AnchorY;
            // 脉冲放大用的版本（null 表示不放大）
            public Bitmap BigSprite;
            public IntPtr BigBitmap;
            public int BigAnchorX;
            public int BigAnchorY;
        }

        /// <summary>造一个 48x48 全透明的光标句柄（内存里，不落盘）。</summary>
        private static IntPtr CreateBlankCursor(int size)
        {
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                IntPtr color = bmp.GetHbitmap(Color.FromArgb(0));
                // AND 掩码：位为 1 = 与屏幕做"或"（保留背景，即透明）；0 = 使用颜色。
                // 之前填的全 0，透明像素就被画成了黑色方块。
                byte[] allOnes = new byte[(size * size + 7) / 8];
                for (int i = 0; i < allOnes.Length; i++) allOnes[i] = 0xFF;
                IntPtr mask = CreateBitmap(size, size, 1, 1, allOnes);
                ICONINFO info = new ICONINFO
                {
                    fIcon = false,
                    xHotspot = 0,
                    yHotspot = 0,
                    hbmMask = mask,
                    hbmColor = color,
                };
                return CreateIconIndirect(ref info);
            }
        }

        private static bool BlankSystemArrow(int size)
        {
            IntPtr blank = CreateBlankCursor(size);
            if (blank == IntPtr.Zero) return false;
            // SetSystemCursor 成功后系统接管这个句柄，不能再 Destroy。
            return SetSystemCursor(blank, OCR_NORMAL);
        }

        private static bool RestoreSystemCursors()
        {
            // 从用户注册表方案重新加载。我们从不写注册表，所以这一步是幂等的。
            return SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }

        private static bool calibrateMode = false;
        private static bool poseMode = false;

        private static Pose LoadPose(string path, int anchorX, int anchorY, int displaySize)
        {
            using (Bitmap src = new Bitmap(path))
            {
                double k = (double)displaySize / Math.Max(src.Width, src.Height);
                int w = Math.Max(1, (int)Math.Round(src.Width * k));
                int h = Math.Max(1, (int)Math.Round(src.Height * k));
                Bitmap scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                double bigK = (double)(int)Math.Round(displaySize * 1.56) / Math.Max(src.Width, src.Height);
                int bw = Math.Max(1, (int)Math.Round(src.Width * bigK));
                int bh = Math.Max(1, (int)Math.Round(src.Height * bigK));
                Bitmap big = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
                using (Graphics g2 = Graphics.FromImage(big))
                {
                    g2.Clear(Color.Transparent);
                    g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g2.DrawImage(src, new Rectangle(0, 0, bw, bh));
                }
                if (calibrateMode)
                {
                    scaled = MarkAnchor(scaled, (int)Math.Round(anchorX * k), (int)Math.Round(anchorY * k));
                }
                return new Pose
                {
                    Sprite = scaled,
                    HBitmap = scaled.GetHbitmap(Color.FromArgb(0)),
                    AnchorX = (int)Math.Round(anchorX * k),
                    AnchorY = (int)Math.Round(anchorY * k),
                    BigSprite = big,
                    BigBitmap = big.GetHbitmap(Color.FromArgb(0)),
                    BigAnchorX = (int)Math.Round(anchorX * bigK),
                    BigAnchorY = (int)Math.Round(anchorY * bigK),
                };
            }
        }

        /// <summary>校准用：在锚点上画红色十字，标出"真正的点击点"。</summary>
        private static Bitmap MarkAnchor(Bitmap sprite, int ax, int ay)
        {
            Bitmap marked = new Bitmap(sprite);
            using (Graphics g = Graphics.FromImage(marked))
            using (Pen p = new Pen(Color.Red, 1))
            {
                g.DrawLine(p, ax - 6, ay, ax + 6, ay);
                g.DrawLine(p, ax, ay - 6, ax, ay + 6);
            }
            return marked;
        }

        private static void DrawAt(IntPtr hwnd, Pose pose, int cursorX, int cursorY, bool big)
        {
            Bitmap sprite = big ? pose.BigSprite : pose.Sprite;
            IntPtr hbmp = big ? pose.BigBitmap : pose.HBitmap;
            int ax = big ? pose.BigAnchorX : pose.AnchorX;
            int ay = big ? pose.BigAnchorY : pose.AnchorY;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr old = IntPtr.Zero;
            try
            {
                old = SelectObject(memDc, hbmp);
                POINT dst = new POINT { X = cursorX - ax, Y = cursorY - ay };
                POINT src = new POINT { X = 0, Y = 0 };
                SIZE size = new SIZE { CX = sprite.Width, CY = sprite.Height };
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA,
                };
                UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static int Main(string[] args)
        {
            SetProcessDPIAware();

            // 保命模式：任何情况下都能把光标恢复回来。
            if (args.Length >= 1 && args[0] == "--restore")
            {
                bool ok = RestoreSystemCursors();
                Console.Error.WriteLine("restore=" + ok);
                return ok ? 0 : 1;
            }

            bool blankMode = args.Length >= 1 && args[0] == "--blank";
            bool calibrate = args.Length >= 1 && args[0] == "--calibrate";
            bool watchMode = args.Length >= 1 && args[0] == "--watch";
            string watchDir = null;
            if (watchMode)
            {
                // --watch <状态目录> <尺寸> <png@x,y> [...]
                // --watch <状态目录> <blank|pose> <尺寸> <png@x,y> [...]
                if (args.Length < 5) { Console.Error.WriteLine("用法: dsc-fx.exe --watch <dir> <blank|pose> <size> <png@x,y> ..."); return 2; }
                watchDir = args[1];
                poseMode = args[2] == "pose";
                if (poseMode) { watchMode = true; }
            }
            int argOffset = blankMode ? 1 : (calibrate ? 1 : 0);

            if (args.Length < argOffset + 3)
            {
                Console.Error.WriteLine("用法: dsc-fx.exe [--blank] <显示尺寸> <总秒数> <png@anchorX,anchorY> [更多...]");
                Console.Error.WriteLine("      dsc-fx.exe --restore");
                return 2;
            }

            calibrateMode = calibrate;
            int size = int.Parse(args[watchMode ? 3 : argOffset]);
            int seconds = watchMode ? 0 : int.Parse(args[argOffset + 1]);
            int poseStart = watchMode ? 4 : (argOffset + 2);

            List<Pose> poses = new List<Pose>();
            for (int i = poseStart; i < args.Length; i++)
            {
                string token = args[i];
                int at = token.LastIndexOf('@');
                if (at < 0) { Console.Error.WriteLine("姿势格式应为 png@x,y: " + token); return 2; }
                string path = token.Substring(0, at);
                string[] xy = token.Substring(at + 1).Split(',');
                poses.Add(LoadPose(path, int.Parse(xy[0]), int.Parse(xy[1]), size));
            }
            if (poses.Count == 0) return 2;

            IntPtr hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
                "STATIC", "dsc-fx", WS_POPUP, 0, 0, poses[0].Sprite.Width, poses[0].Sprite.Height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Console.Error.WriteLine("CreateWindowEx 失败: " + Marshal.GetLastWin32Error());
                return 3;
            }
            // 让这个窗口进不了截图：否则它会被 CopyFromScreen 拍到，
            // 基线 PNG 里带着鱼，光标一动复查就整片"内容变了"。
            // 校准模式要能被截到，否则我量不了偏差。
            if (!calibrate) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            ShowWindow(hwnd, SW_SHOWNOACTIVATE);

            if (blankMode)
            {
                bool blanked = BlankSystemArrow(48);
                Console.Error.WriteLine("blank=" + blanked);
            }

            if (watchMode)
            {
                // ── 常驻模式 ──
                // 插件每次桌面操作写一次 fx.active（内容就是时间戳）。
                // 文件在 30 秒内被更新过 → 认为"这段 computer use 还在进行"：
                // 透明系统箭头 + 显示鱼。超过 30 秒没动静 → 恢复光标、藏起鱼。
                //
                // 为什么用文件而不是命名管道：崩溃后不需要清理，读方也不需要处理连接状态，
                // 最坏情况就是"文件旧了"，而旧了正好就该恢复。
                string activeFile = System.IO.Path.Combine(watchDir, "fx.active");
                string blankedFile = System.IO.Path.Combine(watchDir, "fx.blanked");
                string stopFile = System.IO.Path.Combine(watchDir, "fx.stop");
                string poseFile = System.IO.Path.Combine(watchDir, "fx.pose");
                const double IdleTimeoutSec = 30.0;
                bool blanked = false;
                bool visible = false;

                try
                {
                    while (!System.IO.File.Exists(stopFile))
                    {
                        bool active = false;
                        try
                        {
                            if (System.IO.File.Exists(activeFile))
                                active = (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(activeFile)).TotalSeconds < IdleTimeoutSec;
                        }
                        catch { }

                        if (active)
                        {
                            if (!poseMode && !blanked)
                            {
                                blanked = BlankSystemArrow(48);
                                System.IO.File.WriteAllText(blankedFile, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                            }
                            if (!visible) { ShowWindow(hwnd, SW_SHOWNOACTIVATE); visible = true; }

                            // pose 模式下插件写 fx.pose（姿势序号）；读失败就用第 0 个。
                            int pick = 0;
                            if (poseMode)
                            {
                                try
                                {
                                    string raw = System.IO.File.ReadAllText(poseFile).Trim();
                                    int n;
                                    if (int.TryParse(raw, out n) && poses.Count > 0) pick = ((n % poses.Count) + poses.Count) % poses.Count;
                                }
                                catch { }
                            }

                            POINT p;
                            if (GetCursorPos(out p)) DrawAt(hwnd, poses[pick], p.X, p.Y, false);
                        }
                        else
                        {
                            if (visible) { ShowWindow(hwnd, SW_HIDE); visible = false; }
                            if (blanked) { RestoreSystemCursors(); blanked = false; try { System.IO.File.Delete(blankedFile); } catch { } }
                        }

                        Thread.Sleep(16);
                    }
                }
                finally
                {
                    // 任何退出路径都要恢复：正常退出、异常、被 fx.stop 叫停。
                    if (blanked) RestoreSystemCursors();
                    try { System.IO.File.Delete(blankedFile); } catch { }
                    DestroyWindow(hwnd);
                    foreach (Pose pose in poses)
                    {
                        if (pose.HBitmap != IntPtr.Zero) DeleteObject(pose.HBitmap);
                        pose.Sprite.Dispose();
                    }
                }
                return 0;
            }

            int switchMs = Math.Max(1200, seconds * 1000 / poses.Count);
            int index = 0;
            DateTime start = DateTime.UtcNow;
            DateTime lastSwitch = start;
            int frames = 0;

            while ((DateTime.UtcNow - start).TotalSeconds < seconds)
            {
                POINT p;
                if (GetCursorPos(out p))
                {
                    // 脉冲：每 2.5 秒"放大 0.5 秒"，模拟点击/拖拽时的强调。
                    double t = (DateTime.UtcNow - start).TotalSeconds;
                    // 验证模式（--blank）不要脉冲：它会让画面一大一小地跳。
                    bool pulse = !blankMode && (t % 2.5) < 0.5;
                    DrawAt(hwnd, poses[index], p.X, p.Y, pulse);
                    frames++;
                }
                if ((DateTime.UtcNow - lastSwitch).TotalMilliseconds >= switchMs)
                {
                    index = (index + 1) % poses.Count;
                    lastSwitch = DateTime.UtcNow;
                }
                Thread.Sleep(16);
            }

            if (blankMode) RestoreSystemCursors();
            DestroyWindow(hwnd);
            foreach (Pose pose in poses)
            {
                if (pose.HBitmap != IntPtr.Zero) DeleteObject(pose.HBitmap);
                pose.Sprite.Dispose();
            }
            Console.Error.WriteLine("frames=" + frames + " poses=" + poses.Count);
            return 0;
        }
    }
}
