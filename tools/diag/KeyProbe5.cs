using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 测一件事：低层键盘钩子在工作期间，**窗口是否需要焦点**。
//
// 为什么这个问题是当前的关键：
//   · KeyProbe4（有焦点）→ 钩子回调 11 次，ESC 被捕获
//   · dsc-edge（WS_EX_NOACTIVATE，刻意不抢焦点）→ 回调入口 0 次
// 两个程序用同一套 API、同一台机器、同样从 shell 启动。
// 如果差异真是"焦点"，那 dsc-edge 的设计前提就是错的，
// 必须给钩子单独一个能拿焦点的宿主。
//
// 做法：窗口显示后，先记录"有焦点时"的计数，再提供一个按钮主动放弃焦点，
// 之后每 3 秒把两个计数一起写进日志。这样"失焦前 / 失焦后"是否都触发，
// 从日志里一眼可读。
//
// 用法：KeyProbe5 <秒数> <日志>
class KeyProbe5
{
    private delegate IntPtr LLProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, LLProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(IntPtr n);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    static int _entries, _escSeen;
    static LLProc _keep;
    static string _log = @"D:\dsh workspace\dsh-desktop-control\.state\keyprobe5.log";
    static DateTime _t0 = DateTime.Now;

    static void L(string s)
    {
        try { File.AppendAllText(_log, "[" + (int)(DateTime.Now - _t0).TotalMilliseconds + "ms] " + s + "\r\n"); }
        catch { }
    }

    static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _entries++;                       // 入口即计数，不过滤
        try
        {
            if (nCode >= 0)
            {
                int m = wParam.ToInt32();
                if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    if (vk == 0x1B) _escSeen++;
                }
            }
        }
        catch { }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    sealed class W : Form
    {
        public Label Lbl;
        public IntPtr Hook;
        public bool Defocused;

        public W()
        {
            Text = "焦点测试 — 先按键，再点掉焦点，再按键";
            ClientSize = new Size(660, 260);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Color.FromArgb(16, 28, 54);
            ForeColor = Color.White;

            Lbl = new Label();
            Lbl.Dock = DockStyle.Top;
            Lbl.Height = 150;
            Lbl.Font = new Font("Consolas", 11f);
            Lbl.ForeColor = Color.FromArgb(180, 230, 255);
            Lbl.Padding = new Padding(12);
            Controls.Add(Lbl);

            Button b = new Button();
            b.Text = "点这里放弃焦点（然后按 ESC）";
            b.Font = new Font("Microsoft YaHei UI", 11f);
            b.SetBounds(190, 175, 280, 44);
            b.Click += delegate
            {
                // 放弃焦点：焦点交给桌面，本窗口不再激活
                Defocused = true;
                LS("刚失焦");
                Hide();
                Show();
            };
            Controls.Add(b);
        }

        public void Redraw()
        {
            bool fg = GetForegroundWindow() == Handle;
            Lbl.Text =
                "回调入口次数 = " + _entries + "\r\n" +
                "其中 ESC     = " + _escSeen + "\r\n" +
                "本窗口有焦点 = " + (fg ? "是" : "否") + "\r\n" +
                "已主动失焦   = " + (Defocused ? "是" : "否") + "\r\n" +
                "\r\n步骤：① 现在按几次 ESC   ② 点上面按钮失焦   ③ 再按几次 ESC";
        }
    }

    static W _f;

    static void LS(string tag)
    {
        bool fg = _f != null && GetForegroundWindow() == _f.Handle;
        L(tag + " 入口=" + _entries + " ESC=" + _escSeen + " 有焦点=" + (fg ? "是" : "否"));
    }

    [STAThread]
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 60;
        if (args.Length > 1) _log = args[1];
        try { if (File.Exists(_log)) File.Delete(_log); } catch { }
        L("=== 启动 ===");

        Application.EnableVisualStyles();
        _f = new W();

        _f.Shown += delegate
        {
            _keep = HookProc;
            _f.Hook = SetWindowsHookExW(WH_KEYBOARD_LL, _keep, GetModuleHandleW(IntPtr.Zero), 0);
            L("钩子安装 → " + (_f.Hook == IntPtr.Zero ? "失败 err=" + Marshal.GetLastWin32Error() : "成功"));
            _f.Redraw();

            var ui = new System.Windows.Forms.Timer { Interval = 200 };
            ui.Tick += delegate { _f.Redraw(); };
            ui.Start();

            var beat = new System.Windows.Forms.Timer { Interval = 3000 };
            beat.Tick += delegate { LS("心跳"); };
            beat.Start();

            var end = new System.Windows.Forms.Timer { Interval = secs * 1000 };
            end.Tick += delegate
            {
                end.Stop(); ui.Stop(); beat.Stop();
                L("=== 结束 ===");
                L("最终：入口=" + _entries + " ESC=" + _escSeen);
                _f.Close();
            };
            end.Start();
        };

        Application.Run(_f);
        return 0;
    }
}
