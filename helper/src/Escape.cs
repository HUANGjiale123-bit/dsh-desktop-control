using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DscEdge
{
    /// <summary>
    /// 逃生窗：贴在屏幕最上方的一个小条。
    ///
    /// 文字固定为"您的电脑正在被 deepseek 控制 · 强制退出请按 ESC"，
    /// 让人一眼知道两件事：**现在被谁控制**、**怎么停**。
    ///
    /// ── 为什么是这个形态，而不是全屏窗口 ──
    ///
    /// 最初做的是全屏窗口 + 动态切换鼠标穿透位（光标在按钮上时可点、否则穿透）。
    /// 实测**看不见**：窗口创建成功、IsWindowVisible 也返回 True，
    /// 但扩展样式带上 WS_EX_LAYERED 配上 TransparencyKey 之后整张窗口渲染成全透明。
    ///
    /// 更要紧的是那个设计本身就不对：全屏窗口必须挡住所有应用才能接受点击，
    /// 而"逃生"最不能承受的就是"按钮在某些情况下点不到"。
    /// 现在改成：
    ///   · **不用** WS_EX_LAYERED、**不用** TransparencyKey —— 普通窗口，
    ///     渲染路径只有一条，不可能意外变透明
    ///   · 只占屏幕顶部一条，不铺满、不挡中间
    ///   · 位置固定在顶部居中 —— 恒定位置比"跟着鼠标跑"更容易形成肌肉记忆
    ///
    /// ── 停止方式 ──
    ///
    /// 主路径是**物理 ESC 键**（见 EscapeGate 的低层钩子；实测可用，
    /// 之前判定"钩子不触发"是误判——真因是日志写在只读位置导致回调被异常吞掉）。
    /// 这个条上同时提供一个可点区域，作为"钩子万一没装上"的后备。
    /// 关掉这个窗口也算停止。
    /// </summary>
    internal sealed class EscapeForm : Form
    {
        private readonly Action _onStop;
        private readonly string _label;
        private readonly System.Windows.Forms.Timer _timer;
        private Rectangle _buttonRect;
        private bool _hover, _pressed, _stopping;
        private double _phase;

        private const int BAR_HEIGHT = 46;

        public EscapeForm(string label, Action onStop)
        {
            _label = string.IsNullOrEmpty(label) ? "deepseek" : label;
            _onStop = onStop;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;          // 任务栏可见：万一被盖住还能找回来
            TopMost = true;
            DoubleBuffered = true;
            Text = "computer use — 逃生条";

            int w = Math.Min(760, (int)(Screen.PrimaryScreen.Bounds.Width * 0.55));
            if (w < 460) w = 460;
            ClientSize = new Size(w, BAR_HEIGHT);

            // 顶部居中。用工作区而不是整屏——整屏在多显示器下会把条放到最左边那块屏。
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + 6);

            _timer = new System.Windows.Forms.Timer { Interval = 50 };
            _timer.Tick += delegate { _phase += 0.06; Invalidate(); };

            // 键盘路径：条自己拿到焦点时，ESC / 回车也算停止。
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    e.Handled = true;
                    Stop();
                }
            };
        }

        private void Stop()
        {
            if (_stopping) return;      // 连点或重复触发只停一次
            _stopping = true;
            _timer.Stop();
            try { _onStop(); }
            finally { Close(); }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 只有 WS_EX_NOACTIVATE：不抢焦点，用户可以继续用底下的应用。
                // 刻意**不加** WS_EX_LAYERED / WS_EX_TRANSPARENT —— 那两个是
                // "渲染成全透明"的元凶，而这个条必须看得见。
                cp.ExStyle |= 0x08000000;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 直接关掉这个条 = 也要停止。
            // 不能让"关窗口"变成"只是关了个提示、computer use 还在跑"——
            // 那会让人以为已经停了。
            if (!_stopping)
            {
                _stopping = true;
                _timer.Stop();
                try { _onStop(); } catch { }
            }
            base.OnFormClosing(e);
        }

        /* ───────────────── 绘制 ───────────────── */

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 60 || h < 20) return;

            // 深蓝渐变底：与参考图同一族颜色，但压暗，避免长时间盯着刺眼
            using (GraphicsPath p = RoundRect(new Rectangle(0, 0, w, h), 10))
            using (LinearGradientBrush b = new LinearGradientBrush(
                new Rectangle(0, 0, w, h),
                Color.FromArgb(252, 10, 30, 74),
                Color.FromArgb(252, 22, 62, 138),
                18f))
                g.FillPath(b, p);

            // 细星点：极轻，只让纯色有质感，不影响读字
            for (int i = 0; i < 90; i++)
            {
                float fx = Hash(i, 1) * w;
                float fy = Hash(i, 2) * h;
                float tw = 0.35f + 0.65f * (float)Math.Abs(
                    Math.Sin(_phase * (0.5f + Hash(i, 3)) + Hash(i, 4) * 6.28f));
                int a = (int)(70 * tw);
                float r = 0.6f + Hash(i, 5) * 0.9f;
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(a, 200, 226, 255)))
                    g.FillEllipse(sb, fx - r, fy - r, r * 2, r * 2);
            }

            // 描边：呼吸感的淡蓝光
            int edge = (int)(110 + 70 * Math.Abs(Math.Sin(_phase * 0.7)));
            using (GraphicsPath p = RoundRect(new Rectangle(0, 0, w - 1, h - 1), 10))
            using (Pen pen = new Pen(Color.FromArgb(edge, 140, 200, 255), 1.6f))
                g.DrawPath(pen, p);

            using (Font fMain = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
            using (Font fHint = new Font("Microsoft YaHei UI", 10f))
            {
                string s1 = "您的电脑正在被 " + _label + " 控制";
                string s2 = "  ·  强制退出请按 ";
                string s3 = "ESC";

                SizeF m1 = g.MeasureString(s1, fMain);
                SizeF m2 = g.MeasureString(s2, fHint);
                SizeF m3 = g.MeasureString(s3, fMain);

                float total = m1.Width + m2.Width + m3.Width + 20;
                float x = (w - total) / 2f;
                float y1 = (h - m1.Height) / 2f;
                float y2 = (h - m2.Height) / 2f;

                using (SolidBrush b = new SolidBrush(Color.FromArgb(236, 245, 255)))
                {
                    g.DrawString(s1, fMain, b, x, y1);
                    x += m1.Width;
                    g.DrawString(s2, fHint, b, x, y2);
                    x += m2.Width;
                }

                // ESC 键帽：画成一个小圆角块，比纯文字更像"可以按的东西"
                Rectangle cap = new Rectangle((int)x, (int)(h - m3.Height) / 2 - 4,
                                              (int)m3.Width + 16, (int)m3.Height + 8);
                _buttonRect = cap;
                using (GraphicsPath p = RoundRect(cap, 6))
                using (SolidBrush b = new SolidBrush(
                    _pressed ? Color.FromArgb(255, 226, 78, 78)
                             : _hover ? Color.FromArgb(255, 196, 58, 58)
                                      : Color.FromArgb(255, 168, 42, 42)))
                    g.FillPath(b, p);
                using (GraphicsPath p = RoundRect(cap, 6))
                using (Pen pen = new Pen(Color.FromArgb(190, 255, 200, 200), 1.2f))
                    g.DrawPath(pen, p);
                using (SolidBrush b = new SolidBrush(Color.White))
                    g.DrawString(s3, fMain, b, cap.X + 8, cap.Y + 3);
            }
        }

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

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = _buttonRect.Contains(e.Location);
            if (over != _hover)
            {
                _hover = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_buttonRect.Contains(e.Location)) { _pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_pressed && _buttonRect.Contains(e.Location))
            {
                _pressed = false;
                Stop();          // 按下+抬起都在按钮内才算，避免误触
                return;
            }
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }
    }
}
