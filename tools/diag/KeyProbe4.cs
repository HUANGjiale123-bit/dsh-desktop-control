using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 键盘可达性探针 —— "不可能被忽略"版。
//
// 前两版都得到"三条路全 0"，但无法区分两种情况：
//   (a) 键盘事件真的到不了这个进程
//   (b) 用户根本没看见窗口 / 没按键
//
// 这一版针对 (b) 做了设计：
//   · 窗口大、深色底、亮字，置顶
//   · **带一个输入框** —— 用户敲字能亲眼看到字进去，这就证明"按键确实被这个进程收到了"
//   · 倒计时用大字显示，看得见还剩多久
//   · 每个事件同时写日志文件
//
// 判读：
//   输入框里出现字     → 键盘消息到得了这个进程，WM_KEYDOWN 那条路通
//   钩子回调次数 > 0   → 低层钩子可用，ESC 全局捕获可行
//   WM_HOTKEY > 0      → RegisterHotKey 可用
//   全都没反应         → 键盘确实到不了这个进程
//
// 用法：KeyProbe4 <秒数> <日志文件>
class KeyProbe4
{
    private delegate IntPtr LLProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, LLProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr h, int id);

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_HOTKEY = 0x0312;

    static int _hookCount;
    static LLProc _keep;
    static string _log = @"D:\dsh workspace\dsh-desktop-control\.state\keyprobe4.log";
    static readonly object _lk = new object();
    static DateTime _t0 = DateTime.Now;
    static int _wndCount;

    static void L(string s)
    {
        lock (_lk)
        {
            try { File.AppendAllText(_log, "[" + (int)(DateTime.Now - _t0).TotalMilliseconds + "ms] " + s + "\r\n"); }
            catch { }
        }
    }

    static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int m = wParam.ToInt32();
            if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
            {
                _hookCount++;
                if (_hookCount <= 60)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    L("钩子 #" + _hookCount + " vk=0x" + vk.ToString("X2") + (vk == 0x1B ? " <== ESC" : ""));
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    sealed class Win : Form
    {
        public TextBox Box;
        public Label Status;
        public System.Windows.Forms.Timer Ui;
        public int Hotkey;
        public string HookNote = "未装", HotkeyNote = "未注册";

        public Win()
        {
            Text = "键盘测试 —— 请在下面的框里打字";
            ClientSize = new Size(720, 320);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Color.FromArgb(18, 30, 58);
            ForeColor = Color.White;
            KeyPreview = true;

            Label t = new Label();
            t.Text = "① 在下面这个框里打字（A、空格、ESC 都行）\r\n" +
                     "② 字能进去 = 键盘消息到得了这个进程\r\n" +
                     "③ 看下面两行数字有没有涨";
            t.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(230, 240, 255);
            t.SetBounds(24, 18, 680, 100);
            Controls.Add(t);

            Box = new TextBox();
            Box.Font = new Font("Consolas", 18f);
            Box.SetBounds(24, 126, 672, 46);
            Box.BackColor = Color.White;
            Controls.Add(Box);

            Status = new Label();
            Status.Font = new Font("Consolas", 12f);
            Status.ForeColor = Color.FromArgb(150, 230, 180);
            Status.SetBounds(24, 186, 680, 120);
            Controls.Add(Status);

            Ui = new System.Windows.Forms.Timer { Interval = 120 };
            Ui.Tick += delegate { Redraw(); };
        }

        public void Redraw()
        {
            Status.Text =
                "钩子回调次数  = " + _hookCount + "      （涨了 = ESC 全局捕获可行）\r\n" +
                "WM_HOTKEY     = " + Hotkey + "      （涨了 = RegisterHotKey 可行）\r\n" +
                "窗口按键次数  = " + _wndCount + "\r\n" +
                "钩子: " + HookNote + "    热键: " + HotkeyNote + "\r\n" +
                "剩余秒数      = " + Remain;
        }

        public int Remain = 0;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_KEYDOWN) { _wndCount++; if (_wndCount <= 60) L("窗口 WM_KEYDOWN vk=0x" + m.WParam.ToInt32().ToString("X2")); }
            else if (m.Msg == WM_SYSKEYDOWN) { _wndCount++; L("窗口 WM_SYSKEYDOWN"); }
            else if (m.Msg == WM_HOTKEY) { Hotkey++; L("*** WM_HOTKEY ***"); }
            base.WndProc(ref m);
        }

        public void Setup()
        {
            _keep = HookProc;
            IntPtr h = SetWindowsHookExW(WH_KEYBOARD_LL, _keep, GetModuleHandleW(IntPtr.Zero), 0);
            if (h == IntPtr.Zero) { int e = Marshal.GetLastWin32Error(); HookNote = "装不上 err=" + e; }
            else { HookNote = "已装上"; }
            L("钩子: " + HookNote);

            if (RegisterHotKey(Handle, 1, 0x4000, 0x1B)) { HotkeyNote = "成功"; }
            else { HotkeyNote = "失败 err=" + Marshal.GetLastWin32Error(); }
            L("热键: " + HotkeyNote);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 45;
        if (args.Length > 1) _log = args[1];

        try
        {
            try { if (File.Exists(_log)) File.Delete(_log); } catch { }
            L("=== 启动 时长=" + secs + "s 日志=" + _log);

            Application.EnableVisualStyles();
            Win f = new Win();
            f.Remain = secs;

            f.Shown += delegate
            {
                L("窗体已显示");
                f.Setup();
                f.Box.Focus();
                f.Redraw();

                var ui = new System.Windows.Forms.Timer { Interval = 120 };
                ui.Tick += delegate { f.Redraw(); };
                ui.Start();

                var end = new System.Windows.Forms.Timer { Interval = secs * 1000 };
                end.Tick += delegate
                {
                    end.Stop(); ui.Stop();
                    L("");
                    L("=== 汇总 ===");
                    L("钩子回调次数 = " + _hookCount);
                    L("WM_HOTKEY    = " + f.Hotkey);
                    L("窗口按键次数 = " + _wndCount);
                    L("输入框内容   = " + (f.Box.Text.Length > 60 ? f.Box.Text.Substring(0, 60) : f.Box.Text));
                    L("输入框字符数 = " + f.Box.Text.Length);
                    L("");
                    if (f.Box.Text.Length > 0)
                        L("结论：键盘消息到得了这个进程（输入框收到了字）");
                    else
                        L("结论：连输入框都没收到字 —— 键盘确实到不了这个进程");
                    if (_hookCount > 0) L("附加：低层钩子可用，ESC 全局捕获可行");
                    if (f.Hotkey > 0) L("附加：RegisterHotKey 可用");
                    f.Close();
                };
                end.Start();
            };

            Application.Run(f);
            return 0;
        }
        catch (Exception e)
        {
            L("!! 异常: " + e);
            return 1;
        }
    }
}
