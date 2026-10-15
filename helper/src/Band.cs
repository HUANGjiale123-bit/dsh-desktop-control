using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DscEsc
{
    /// <summary>
    /// 屏幕边缘的状态光带。
    ///
    /// ── 画法：三层叠加，没有粒子 ──
    ///
    ///   ① 边界线  屏幕最外一圈 1~2px 的亮线，清晰框出边界
    ///   ② 内发光  从边缘向内约 22px 的渐变，由亮到全透明
    ///   ③ 流光    一小段更亮的高光沿周长缓慢移动，让画面"活着"
    ///   ④ 四角    短亮线，给直角处一点锚点
    ///
    /// 旧版沿周长铺 180~560 个发光点，点与点靠外晕勉强相连，
    /// 密度一变就露馅，浅色背景上像脏点。连续渐变不会有这个问题。
    ///
    /// ── 为什么用 UpdateLayeredWindow 而不是 TransparencyKey ──
    ///
    /// 两种透明方案都实测失败，失败方式很说明问题：
    ///
    ///   带 WS_EX_LAYERED + TransparencyKey → 整圈洋红原样显示
    ///      抓屏采样到 (228,23,255) 纯色。透明键是 GDI 层机制，
    ///      窗口交给 DWM 合成后它不再生效。
    ///   不带 WS_EX_LAYERED + TransparencyKey → 同样显示洋红
    ///      采样 (219,37,255)。
    ///
    /// 所以改成**分层窗口 + UpdateLayeredWindow 逐像素 alpha**：
    /// 自己提供一张带 alpha 的位图，系统直接拿它合成，
    /// 不经过"透明键"那条会被绕过的路径。
    /// </summary>
    internal sealed class BandForm : Form
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObj);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int i);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private const int ULW_ALPHA = 2, AC_SRC_ALPHA = 1;

        private readonly Color _accent;
        private readonly System.Windows.Forms.Timer _anim;
        private readonly Bitmap _canvas;
        private double _phase;
        private double _flow;
        private float _scale;

        public BandForm(Color accent)
        {
            _accent = accent;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Text = "dsh-band";

            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0) { vw = Screen.PrimaryScreen.Bounds.Width; vx = 0; }
            if (vh <= 0) { vh = Screen.PrimaryScreen.Bounds.Height; vy = 0; }

            Bounds = new Rectangle(vx, vy, vw, vh);
            _canvas = new Bitmap(vw, vh, PixelFormat.Format32bppArgb);

            // 以 1920 宽为基准缩放并夹住：太小看不见，太大喧宾夺主
            _scale = vw / 1920f;
            if (_scale < 0.75f) _scale = 0.75f;
            if (_scale > 2.0f) _scale = 2.0f;

            _anim = new System.Windows.Forms.Timer { Interval = 33 };   // ~30fps
            _anim.Tick += delegate
            {
                _phase += 0.035;
                _flow += 0.0011;
                if (_flow > 1.0) _flow -= 1.0;
                Render();
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_LAYERED 是 UpdateLayeredWindow 的前提；
                // 穿透与不抢焦点保证它不干扰底下的应用。
                cp.ExStyle |= 0x00080000   // WS_EX_LAYERED
                            | 0x00000020   // WS_EX_TRANSPARENT  点击穿透
                            | 0x00000080   // WS_EX_TOOLWINDOW   不占 Alt+Tab
                            | 0x08000000;  // WS_EX_NOACTIVATE   不抢焦点
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();          // 先出一帧，避免显示瞬间空白
            _anim.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _anim.Stop();
            base.OnFormClosed(e);
        }

        // 窗体不做常规绘制：内容全部经 UpdateLayeredWindow 提交
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        /* ───────────────── 渲染 ───────────────── */

        private void Render()
        {
            if (!IsHandleCreated) return;

            int w = _canvas.Width, h = _canvas.Height;

            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingMode = CompositingMode.SourceOver;
                g.Clear(Color.Transparent);     // 全透明起点：只有画上去的地方可见
                PaintBand(g, w, h);
            }

            PushToScreen();
        }

        /// <summary>把 _canvas 作为带 alpha 的位图提交给系统合成。</summary>
        private void PushToScreen()
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            IntPtr oldBmp = IntPtr.Zero;
            try
            {
                hBmp = _canvas.GetHbitmap(Color.FromArgb(0));
                oldBmp = SelectObject(memDc, hBmp);

                POINT dst = new POINT { X = Left, Y = Top };
                POINT src = new POINT { X = 0, Y = 0 };
                SIZE size = new SIZE { cx = _canvas.Width, cy = _canvas.Height };
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = 0,               // AC_SRC_OVER
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };

                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                                    memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void PaintBand(Graphics g, int w, int h)
        {
            float glow = 40f * _scale;
            float line = Math.Max(1f, 1.6f * _scale);

            // 呼吸幅度刻意小：长时间盯着不该累
            float breath = 0.84f + 0.16f * (float)Math.Sin(_phase);

            DrawInnerGlow(g, w, h, glow, breath);
            DrawEdgeLine(g, w, h, line, breath);
            DrawFlowingHighlight(g, w, h, glow, line);
            DrawCornerAccents(g, w, h, line, breath);
        }

        /// <summary>
        /// 内发光：四条边各一条从边缘向内的线性渐变。
        ///
        /// 分开画四条而不是一个"外框"：线性渐变刷只能沿一个方向变化，
        /// 四条边方向不同。角上会重叠，靠峰值透明度压低避免出现亮块。
        /// </summary>
        private void DrawInnerGlow(Graphics g, int w, int h, float glow, float breath)
        {
            int a = (int)(175 * breath);
            if (a < 6) a = 6;
            // 用淡蓝白而不是纯主色：主色在深色背景上偏暗、糊成一片，
            // 只有往白里提才读得出"发光"。这是观感上的关键一步。
            // 光晕用**固定的亮蓝**，而不是从主色提亮。
            //
            // 原因：主色是深海军蓝（#304090，取自表情包图库的配色），
            // 照比例提亮后仍偏暗，叠在深色桌面上会**变暗而不是发光**
            // ——这个坑今天踩过一次。发光需要"高亮度 + 中等饱和"，
            // 所以直接取一个亮蓝，但色相与主色一致，整体仍是同一套配色。
            Color inner = Color.FromArgb(0x6E, 0x9E, 0xFF);
            Color c0 = Color.FromArgb(a, inner);
            Color c1 = Color.FromArgb(0, inner);

            using (LinearGradientBrush b = new LinearGradientBrush(
                new RectangleF(0, 0, w, glow), c0, c1, LinearGradientMode.Vertical))
                g.FillRectangle(b, 0, 0, w, glow);

            using (LinearGradientBrush b = new LinearGradientBrush(
                new RectangleF(0, h - glow, w, glow), c1, c0, LinearGradientMode.Vertical))
                g.FillRectangle(b, 0, h - glow, w, glow);

            using (LinearGradientBrush b = new LinearGradientBrush(
                new RectangleF(0, 0, glow, h), c0, c1, LinearGradientMode.Horizontal))
                g.FillRectangle(b, 0, 0, glow, h);

            using (LinearGradientBrush b = new LinearGradientBrush(
                new RectangleF(w - glow, 0, glow, h), c1, c0, LinearGradientMode.Horizontal))
                g.FillRectangle(b, w - glow, 0, glow, h);
        }

        /// <summary>
        /// 边界线：贴着最外沿的一圈细亮线。
        ///
        /// 它的作用是**给出清晰边界**——全是弥散光晕的话边界感很弱，
        /// 看起来像脏而不像框。一条利落的线把"光晕"和"模糊"区分开。
        /// </summary>
        private void DrawEdgeLine(Graphics g, int w, int h, float line, float breath)
        {
            int a = (int)(205 * breath);
            if (a < 20) a = 20;
            using (Pen p = new Pen(Color.FromArgb(a, Lighten(_accent, 0.55f)), line))
            {
                float o = line / 2f;     // 画在外半像素，避免被裁掉一半显虚
                g.DrawRectangle(p, o, o, w - line, h - line);
            }
        }

        /// <summary>
        /// 流光：一小段更亮的光沿周长缓慢移动。
        ///
        /// 静态边框容易被余光忽略，缓慢移动的高光会自然吸引注意，
        /// 但不闪不跳，不干扰正在做的事。用多段递减的短线拼出拖尾。
        /// </summary>
        private void DrawFlowingHighlight(Graphics g, int w, int h, float glow, float line)
        {
            float perim = 2f * (w + h);
            const int SEG = 24;
            float head = (float)(_flow * perim);

            for (int i = 0; i < SEG; i++)
            {
                float t = i / (float)SEG;
                int a = (int)(120 * (1f - t) * (1f - t));
                if (a < 3) continue;

                float p = head - i * (20f * _scale);
                while (p < 0) p += perim;
                p %= perim;

                PointF pt;
                bool vertical;
                if (p < w) { pt = new PointF(p, 0); vertical = false; }
                else if (p < w + h) { pt = new PointF(w, p - w); vertical = true; }
                else if (p < 2 * w + h) { pt = new PointF(w - (p - w - h), h); vertical = false; }
                else { pt = new PointF(0, h - (p - 2 * w - h)); vertical = true; }

                using (Pen pen = new Pen(Color.FromArgb(a, Lighten(_accent, 0.85f)), line * 2.2f))
                {
                    float half = 13 * _scale;
                    if (vertical) g.DrawLine(pen, pt.X, pt.Y - half, pt.X, pt.Y + half);
                    else g.DrawLine(pen, pt.X - half, pt.Y, pt.X + half, pt.Y);
                }
            }
        }

        /// <summary>四角短亮线：让直角处有锚点，不至于显得"断开了"。</summary>
        private void DrawCornerAccents(Graphics g, int w, int h, float line, float breath)
        {
            float len = 110f * _scale;
            int a = (int)(178 * breath);
            if (a < 16) a = 16;
            using (Pen p = new Pen(Color.FromArgb(a, Lighten(_accent, 0.72f)), line * 2f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                float m = line * 1.6f;

                g.DrawLine(p, m, m, len, m);
                g.DrawLine(p, m, m, m, len);
                g.DrawLine(p, w - m, m, w - len, m);
                g.DrawLine(p, w - m, m, w - m, len);
                g.DrawLine(p, m, h - m, len, h - m);
                g.DrawLine(p, m, h - m, m, h - len);
                g.DrawLine(p, w - m, h - m, w - len, h - m);
                g.DrawLine(p, w - m, h - m, w - m, h - len);
            }
        }

        private static Color Lighten(Color c, float k)
        {
            return Color.FromArgb(c.A,
                (int)(c.R + (255 - c.R) * k),
                (int)(c.G + (255 - c.G) * k),
                (int)(c.B + (255 - c.B) * k));
        }
    }
}
