/*
 * dsh-desktop-control — computer use 状态指示层
 *
 * 沿着整个屏幕的边缘漂浮着一层蓝色发光粒子，顶部中央有一枚状态徽标，
 * 表示"computer use 已激活，此刻有人在操作这台电脑"。
 *
 * 视觉：蓝色粒子、有虚幻感。做法
 *
 *   - 每个粒子是"亮芯 + 柔和外晕"的径向渐变，不是实心圆点
 *   - 粒子沿边缘缓慢上浮/漂移，角部自然堆积（用四角吸引算出来的密度，
 *     不靠硬编码），越靠屏幕内侧越淡，像光从边缘渗出去
 *   - 细小的暗粒子做纵深，大小不一的亮粒子做前层
 *   - 整层极缓呼吸，余光能察觉但不构成干扰
 *   - 顶部徽标沿用同一套发光语言，保持整体一致
 *
 * 为什么标的是整屏而不是被操作的窗口：窗口会移动、会被遮挡、会在多个
 * 目标之间切换，跟着它画既不稳定也容易看漏。整屏边缘位置恒定，
 * 不可能被遮住，语义也更准确——它标示"整机正处于被操作状态"。
 *
 * 三条硬性约束：
 *
 *   1. 绝不拦截输入。WS_EX_TRANSPARENT | WS_EX_NOACTIVATE，
 *      点击、滚轮、键盘全部穿透；不抢焦点；不占 Alt+Tab。
 *   2. 尽量不进截图。尝试 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)，
 *      让本层不出现在屏幕录制与截图中——否则助手每次读自己的截图，
 *      都会看到一圈无意义的装饰。系统不支持时只是退化为会被截到。
 *   3. 不抢用户的操作。除了画，什么都不做。
 *
 * 用法：
 *   dsc-edge.exe [--label 文本] [--timeout 毫秒] [--color RRGGBB] [--no-exclude]
 *                [--static]        静止画面，便于截图核对
 */

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DscEdge
{
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool AdjustWindowRectEx(ref RECT lpRect, uint dwStyle, bool bMenu, uint dwExStyle);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    }

    internal sealed class EdgeForm : Form
    {
        private readonly string _label;
        private readonly Color _core;      // 主色（亮）
        private readonly Color _deep;      // 深色（用于外晕与徽标底）
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _pulse;
        private double _phase;
        private double _flow;              // 流光位置 0..1
        private int _ticks;
        private readonly int _timeoutTicks;
        private bool _affinityTried;
        private string _affinityNote = "未尝试";
        private readonly bool _noExclude;
        private readonly bool _static;

        public EdgeForm(string label, int timeoutMs, Color accent, bool noExclude, bool isStatic)
        {
            _label = label;
            _core = accent;
            _deep = Darken(accent, 0.45f);
            _noExclude = noExclude;
            _static = isStatic;
            _timeoutTicks = timeoutMs > 0 ? Math.Max(1, timeoutMs / 50) : 0;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            Text = "dsh-desktop-control";

            _timer = new System.Windows.Forms.Timer { Interval = 50 };
            _timer.Tick += (s, e) => Tick();

            _pulse = new System.Windows.Forms.Timer { Interval = 60 };
            _pulse.Tick += (s, e) =>
            {
                if (_static) return;
                _phase += 0.045;
                _flow += 0.0016; if (_flow > 1.0) _flow -= 1.0;
                Opacity = 0.86 + 0.14 * Math.Abs(Math.Sin(_phase));
            };
        }

        private static Color Darken(Color c, float k)
        {
            return Color.FromArgb(c.A,
                (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000   // WS_EX_LAYERED
                            | 0x00000020   // WS_EX_TRANSPARENT  输入穿透
                            | 0x00000080   // WS_EX_TOOLWINDOW   不占 Alt+Tab
                            | 0x08000000;  // WS_EX_NOACTIVATE   不抢焦点
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CoverWholeScreen();
            TryExcludeFromCapture();
        }

        /// <summary>
        /// 让**客户区**严格等于整个虚拟屏幕。
        ///
        /// 直接按屏幕尺寸设窗口是不行的：Windows 会给顶层窗口加一圈不可见的
        /// 调整边框（在 150% 缩放下约 12px），客户区因此比窗口小一圈，
        /// 画出来的粒子就会与屏幕边之间留一道缝。
        ///
        /// 正确做法是问 AdjustWindowRectEx：给定目标客户区尺寸，它反算出
        /// 窗口该是多大。据此外扩并左移上移，客户区才真正贴住屏幕四边。
        /// </summary>
        private void CoverWholeScreen()
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (vw <= 0) vw = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            if (vh <= 0) vh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            if (vw <= 0 || vh <= 0) return;

            // 目标客户区 = 整个虚拟屏幕；反算窗口外框
            Native.RECT want = new Native.RECT { Left = 0, Top = 0, Right = vw, Bottom = vh };
            uint style = unchecked((uint)GetWindowLong(Handle, -16));      // GWL_STYLE
            uint exStyle = unchecked((uint)GetWindowLong(Handle, -20));    // GWL_EXSTYLE
            try { Native.AdjustWindowRectEx(ref want, style, false, exStyle); } catch { }

            int winW = want.Right - want.Left;
            int winH = want.Bottom - want.Top;
            int winX = vx + want.Left;      // want.Left 通常为负（左边框宽度）
            int winY = vy + want.Top;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, winX, winY, winW, winH,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        /* ── ESC 急停：与本进程合为一体 ──
         *
         * 指示层和急停守候看起来是两件事，但它们的生命周期完全一致：
         * 都是"computer use 活跃期间"存在，都该在那一轮结束时消失。
         * 合成一个进程还解决了一个实际问题——键盘钩子必须由**不在沙箱里的
         * 进程**安装才有效（实测：从受限 shell 启动的进程装了钩子但收不到
         * 任何按键，而真实按键与合成按键都收不到）。由插件在宿主进程里
         * 拉起本进程，钩子才装得成。
         *
         * 纪律：钩子**绝不吞掉 ESC**，回调里永远 CallNextHookEx。
         * 吃掉 ESC 会把这个键从使用者手里夺走——关对话框、退全屏全失效，
         * 那本身就变成新的麻烦。我们只是"顺便知道你按了"，不改变它的去向。
         */


        private void TryExcludeFromCapture()
        {
            if (_affinityTried) return;
            _affinityTried = true;
            if (_noExclude) { _affinityNote = "按 --no-exclude 保留在截图中（仅供外观检查）"; return; }
            try
            {
                if (Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE))
                    _affinityNote = "已从截图中排除";
                else
                    _affinityNote = "系统不支持截图排除(err=" + Marshal.GetLastWin32Error() + ")，会被截入画面";
            }
            catch { _affinityNote = "截图排除不可用"; }
        }

        private void Tick()
        {
            _ticks++;
            if (_timeoutTicks > 0 && _ticks > _timeoutTicks) { Close(); return; }
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
            _pulse.Start();
            Console.Error.WriteLine("[edge] " + _affinityNote);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            int w = Width, h = Height;
            if (w < 80 || h < 80) return;

            // 允许在客户区外作画：贴着边框的粒子有一半在边界之外，
            // 不放开这一层会被裁成半圆，边缘那道"光"就断了。
            float pad = 40f;
            g.SetClip(new RectangleF(-pad, -pad, w + pad * 2, h + pad * 2));

            DrawParticles(g, w, h);
            if (!string.IsNullOrEmpty(_label)) DrawBadge(g, w);
        }

        /* ───────────────── 粒子云 ─────────────────
         *
         * 粒子贴着屏幕四条边漂浮。分布不是均匀的：用"到最近角点的距离"
         * 反推权重，让角部自然变密，四边中段偏疏——看起来像光从四角渗出。
         *
         * 每颗粒子由三部分组成：
         *   外晕  大而极淡，负责虚幻的空气感
         *   中晕  中等，负责体积
         *   亮芯  小而近乎发白，负责"这是个光源"
         */

        private struct Particle
        {
            public float X, Y;       // 基准位置
            public float Size;       // 亮芯半径
            public float Depth;      // 0=最内最淡，1=最贴边最亮
            public float Speed;      // 漂移速度
            public float Phase;      // 相位，避免同步呼吸
            public float DriftX;     // 横向漂移幅度
            public float Twinkle;    // 闪烁强度
        }

        private Particle[] _particles;

        /** 稳定伪随机：同一 index 永远得到同一结果，粒子不会每帧乱跳。 */
        private static float Hash(int i, int salt)
        {
            unchecked
            {
                int x = i * 374761393 + salt * 668265263;
                x = (x ^ (x >> 13)) * 1274126177;
                x = x ^ (x >> 16);
                return (x & 0x7FFFFFFF) / (float)0x7FFFFFFF;
            }
        }

        private void EnsureParticles(int w, int h)
        {
            if (_particles != null) return;

            // 密度按周长定，屏幕越大粒子越多；上限防止超大屏卡顿
            float perim = 2f * (w + h);
            int count = (int)Math.Min(560, Math.Max(180, perim / 5.2f));
            _particles = new Particle[count];

            for (int i = 0; i < count; i++)
            {
                float t = (i + 0.5f) / count;
                float p = t * perim;

                // 沿周长定位，再换算到坐标
                float x, y;
                if (p < w) { x = p; y = 0; }
                else if (p < w + h) { x = w; y = p - w; }
                else if (p < 2 * w + h) { x = w - (p - w - h); y = h; }
                else { x = 0; y = h - (p - 2 * w - h); }

                // 张力：越靠角越亮越密
                float nearCorner = (float)Math.Min(
                    Math.Min(Math.Sqrt(x * x + y * y), Math.Sqrt((w - x) * (w - x) + y * y)),
                    Math.Min(Math.Sqrt(x * x + (h - y) * (h - y)), Math.Sqrt((w - x) * (w - x) + (h - y) * (h - y))));
                float cornerBoost = 1f - Math.Min(1f, nearCorner / (Math.Min(w, h) * 0.45f));
                cornerBoost = cornerBoost * cornerBoost;

                float r1 = Hash(i, 1), r2 = Hash(i, 2), r3 = Hash(i, 3), r4 = Hash(i, 4);

                // 由边向内的偏移。用平方分布让绝大多数粒子紧贴边框，
                // 少数飘向内侧制造纵深——若均匀分布会显得像噪点而非边缘光。
                float inset = r1 * r1 * 14f;

                Particle pt = new Particle();
                if (y < 1) { pt.X = x; pt.Y = inset; }
                else if (x > w - 1) { pt.X = w - inset; pt.Y = y; }
                else if (y > h - 1) { pt.X = x; pt.Y = h - inset; }
                else { pt.X = inset; pt.Y = y; }

                pt.Depth = 1f - Math.Min(1f, inset / 14f);
                pt.Size = (1.1f + r2 * 2.4f) * (0.72f + 0.55f * cornerBoost);
                pt.Speed = 5f + r3 * 16f;
                pt.Phase = r4 * 6.283f;
                pt.DriftX = (Hash(i, 5) - 0.5f) * 16f;
                pt.Twinkle = 0.35f + Hash(i, 6) * 0.65f;

                _particles[i] = pt;
            }
        }

        private void DrawParticles(Graphics g, int w, int h)
        {
            EnsureParticles(w, h);

            float t = (float)_phase;
            Color white = Blend(_core, Color.White, 1f);

            foreach (Particle p in _particles)
            {
                // 上浮：沿边的法线方向轻微向内又向外，形成漂浮感
                float bob = (float)Math.Sin(t * 0.9 + p.Phase) * 3.2f;
                float sway = (float)Math.Sin(t * 0.5 + p.Phase * 1.7) * p.DriftX;

                float px = p.X + sway;
                float py = p.Y + bob;

                // 闪烁：亮度随相位起伏，幅度由 Twinkle 控制
                float f = 0.55f + 0.45f * (float)Math.Sin(t * 1.6 + p.Phase * 2.3);
                float lum = (0.30f + 0.70f * p.Depth) * (1f - p.Twinkle + p.Twinkle * f);

                float size = p.Size;

                // ① 外晕——大而极淡，负责空气感
                DrawGlowDot(g, px, py, size * 9f, Color.FromArgb((int)(16 * lum), _core));
                // ② 中晕——体积
                DrawGlowDot(g, px, py, size * 4.2f, Color.FromArgb((int)(46 * lum), Blend(_core, white, 0.25f)));
                // ③ 亮芯——光源本体
                DrawGlowDot(g, px, py, size, Color.FromArgb((int)(210 * lum), Blend(_core, white, 0.85f)));
            }

            // 边缘再压一层极淡的连续辉光，把散落的粒子"粘"成一条光带，
            // 否则粒子太散会显得像噪点而不是指示
            for (int i = 0; i < 3; i++)
            {
                int t2 = new[] { 14, 7, 3 }[i];
                int a = new[] { 10, 18, 34 }[i];
                using (Pen pen = new Pen(Color.FromArgb(a, _core), t2))
                {
                    pen.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(pen, t2 / 2f, t2 / 2f, w - t2, h - t2);
                }
            }
        }

        /** 一个柔和的发光点：中心实、边缘化开。 */
        private static void DrawGlowDot(Graphics g, float cx, float cy, float r, Color color)
        {
            if (r < 0.4f || color.A <= 0) return;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(cx - r, cy - r, r * 2f, r * 2f);
                using (PathGradientBrush b = new PathGradientBrush(path))
                {
                    b.CenterColor = color;
                    b.SurroundColors = new[] { Color.FromArgb(0, color) };
                    b.CenterPoint = new PointF(cx, cy);
                    g.FillPath(b, path);
                }
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                a.A,
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /* ── 顶部中央徽标 ── */
        private void DrawBadge(Graphics g, int w)
        {
            using (Font f = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular))
            using (Font fb = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
            {
                string prefix = "Computer Use";
                string rest = "　" + _label;

                SizeF s1 = g.MeasureString(prefix, fb);
                SizeF s2 = g.MeasureString(rest, f);

                int dot = 11, padX = 20, padY = 10, gap = 10;
                int bw = (int)(dot + gap + s1.Width + s2.Width) + padX * 2;
                int bh = (int)Math.Max(s1.Height, s2.Height) + padY * 2;
                int x = (w - bw) / 2, y = 16;
                Rectangle box = new Rectangle(x, y, bw, bh);

                // 徽标周围也铺一层柔光，与粒子用同一套发光语言
                DrawGlowDot(g, box.X + box.Width / 2f, box.Y + box.Height / 2f,
                    box.Width * 0.62f, Color.FromArgb(40, _core));

                using (GraphicsPath path = RoundRect(box, bh / 2))
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    box, Color.FromArgb(240, 10, 16, 32), Color.FromArgb(240, 18, 30, 62),
                    LinearGradientMode.Vertical))
                    g.FillPath(bg, path);

                using (GraphicsPath path = RoundRect(box, bh / 2))
                using (Pen border = new Pen(Color.FromArgb(155, Blend(_core, Color.White, 0.4f)), 1.4f))
                    g.DrawPath(border, path);

                float cx = box.X + padX + dot / 2f;
                float cy = box.Y + box.Height / 2f;

                float f2 = 0.7f + 0.3f * (float)Math.Sin(_phase * 1.6);
                DrawGlowDot(g, cx, cy, dot * 1.5f, Color.FromArgb((int)(90 * f2), _core));
                DrawGlowDot(g, cx, cy, dot * 0.55f, Color.FromArgb(235, Blend(_core, Color.White, 0.8f)));

                float tx = cx + dot / 2f + gap;
                float ty = box.Y + padY;

                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 244, 255)))
                    g.DrawString(prefix, fb, b, tx, ty);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(148, 168, 202)))
                    g.DrawString(rest, f, b, tx + s1.Width, ty);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _pulse.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string label = "已激活";
            int timeout = 0;
            bool noExclude = false;
            bool isStatic = false;
            Color accent = Color.FromArgb(0x4D, 0x9A, 0xFF);   // 亮蓝

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--label": if (i + 1 < args.Length) label = args[++i]; break;
                    case "--timeout": if (i + 1 < args.Length) int.TryParse(args[++i], out timeout); break;
                    case "--no-exclude": noExclude = true; break;
                    case "--static": isStatic = true; break;
                    case "--color":
                        if (i + 1 < args.Length)
                        {
                            try { accent = ColorTranslator.FromHtml("#" + args[++i]); }
                            catch { }
                        }
                        break;
                }
            }

            Application.EnableVisualStyles();

            Application.Run(new EdgeForm(label, timeout, accent, noExclude, isStatic));
            return 0;
        }
    }
}
