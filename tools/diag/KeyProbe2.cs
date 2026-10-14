using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// 键盘可达性探针（写文件版）。
//
// 上一版的问题：结果只打到 stdout，而它是 winexe —— 从 bash 后台启动时
// 控制台输出丢失，什么也没留下。这一版**每一步都落到文件**，
// 而且**钩子回调每触发一次就写一行**，这样"回调到底有没有来"是可直接读的，
// 不必靠用户念屏幕。
//
// 同时记三条路，因为它们的修法完全不同：
//   LL 钩子        SetWindowsHookEx(WH_KEYBOARD_LL) —— 全局，不需要焦点
//   RegisterHotKey 系统热键 —— 全局，另一套机制
//   WM_KEYDOWN     窗口消息 —— 只在窗口有焦点时来，作为"键盘消息本身通不通"的对照
//
// 用法：KeyProbe2 <秒数> <输出文件>
class KeyProbe2
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
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_HOTKEY = 0x0312;

    private static int _hookEvents;
    private static LLProc _keepAlive;
    private static string _file;
    private static readonly object _lock = new object();
    private static DateTime _t0;

    private static void Log(string s)
    {
        lock (_lock)
        {
            try { File.AppendAllText(_file, "[" + (int)(DateTime.Now - _t0).TotalMilliseconds + "ms] " + s + Environment.NewLine); }
            catch { }
        }
    }

    private static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                _hookEvents++;
                int vk = Marshal.ReadInt32(lParam);
                // 只记前 40 次，避免用户狂按时把文件写爆
                if (_hookEvents <= 40)
                    Log("钩子回调 #" + _hookEvents + " vk=0x" + vk.ToString("X2") +
                        (vk == 0x1B ? "  ← ESC" : ""));
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private sealed class ProbeForm : Form
    {
        public int KeyDown, SysKeyDown, Hotkey;
        public IntPtr Hook = IntPtr.Zero;
        public string HookNote = "未装";
        public bool HotkeyOk;
        public string HotkeyNote = "未注册";
        private Label _lbl;
        private System.Windows.Forms.Timer _ui;

        public ProbeForm()
        {
            Text = "键盘探针 — 点这个窗口，然后按键";
            ClientSize = new System.Drawing.Size(600, 260);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            KeyPreview = true;
            _lbl = new Label();
            _lbl.Dock = DockStyle.Fill;
            _lbl.Font = new System.Drawing.Font("Consolas", 11f);
            _lbl.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _lbl.Padding = new Padding(14);
            Controls.Add(_lbl);
            _ui = new System.Windows.Forms.Timer { Interval = 100 };
            _ui.Tick += delegate { RefreshText(); };
        }

        public void RefreshText()
        {
            bool fg = GetForegroundWindow() == Handle;
            _lbl.Text =
                "焦点在本窗口: " + (fg ? "是" : "否 —— 先点一下我") + "\r\n" +
                "\r\n【窗口消息】\r\n" +
                "  WM_KEYDOWN      = " + KeyDown + "\r\n" +
                "  WM_SYSKEYDOWN   = " + SysKeyDown + "\r\n" +
                "\r\n【RegisterHotKey(ESC)】 " + HotkeyNote + "\r\n" +
                "  WM_HOTKEY       = " + Hotkey + "\r\n" +
                "\r\n【低层钩子】 " + HookNote + "\r\n" +
                "  回调触发        = " + _hookEvents + "\r\n" +
                "\r\n（按 A / 空格 / ESC 各几次；结果同时写日志文件）";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_KEYDOWN) { KeyDown++; Log("窗口收到 WM_KEYDOWN vk=0x" + m.WParam.ToInt32().ToString("X2")); }
            else if (m.Msg == WM_SYSKEYDOWN) { SysKeyDown++; Log("窗口收到 WM_SYSKEYDOWN vk=0x" + m.WParam.ToInt32().ToString("X2")); }
            else if (m.Msg == WM_HOTKEY) { Hotkey++; Log("★ 收到 WM_HOTKEY（RegisterHotKey 生效）"); }
            base.WndProc(ref m);
        }

        public void Install()
        {
            _keepAlive = HookProc;
            Hook = SetWindowsHookExW(WH_KEYBOARD_LL, _keepAlive, GetModuleHandleW(IntPtr.Zero), 0);
            if (Hook == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                HookNote = "装不上 err=" + e;
                Log("钩子安装失败 err=" + e + " (" + new System.ComponentModel.Win32Exception(e).Message + ")");
            }
            else { HookNote = "已装上"; Log("钩子安装成功 hook=" + Hook); }

            HotkeyOk = RegisterHotKey(Handle, 1, 0x4000, 0x1B);
            if (!HotkeyOk) { int e = Marshal.GetLastWin32Error(); HotkeyNote = "失败 err=" + e; Log("RegisterHotKey 失败 err=" + e); }
            else { HotkeyNote = "成功"; Log("RegisterHotKey(VK_ESCAPE) 成功"); }
        }

        public void Cleanup()
        {
            if (Hook != IntPtr.Zero) UnhookWindowsHookEx(Hook);
            if (HotkeyOk) UnregisterHotKey(Handle, 1);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 60;
        _file = args.Length > 1 ? args[1] : @"D:\keyprobe2.log";
        _t0 = DateTime.Now;

        // 立刻做一次独立的写文件探测：如果这一步就失败，
        // 后面所有日志都会静默丢失，而症状是"程序跑了但什么也没留下"——
        // 那会把"钩子没触发"和"日志写不出去"混成同一个现象，无法排查。
        string probe = @"D:\keyprobe-write-test.txt";
        try
        {
            File.WriteAllText(probe, "write test at " + DateTime.Now.ToString("HH:mm:ss") + "
");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "keyprobe-write-test.txt"), "fallback: " + ex.Message); } catch { }
        }

        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
        Log("=== 键盘探针启动 ===");
        Log("注意：窗口不需要焦点，钩子与热键是全局的");

        Application.EnableVisualStyles();
        var f = new ProbeForm();
        f.Shown += delegate
        {
            f.Install();
            f.RefreshText();
            f.Activate();
            var t = new System.Windows.Forms.Timer { Interval = secs * 1000 };
            t.Tick += delegate
            {
                t.Stop();
                Log("");
                Log("=== 汇总 ===");
                Log("钩子回调次数     = " + _hookEvents);
                Log("WM_KEYDOWN       = " + f.KeyDown);
                Log("WM_SYSKEYDOWN    = " + f.SysKeyDown);
                Log("WM_HOTKEY        = " + f.Hotkey);
                Log("钩子状态         = " + f.HookNote);
                Log("热键状态         = " + f.HotkeyNote);
                Log("");
                Log(_hookEvents > 0
                    ? "结论：★ 低层钩子**可用** —— ESC 全局捕获这条路能走"
                    : "结论：钩子装上但**一次都没触发**");
                f.Cleanup();
                f.Close();
            };
            t.Start();
        };
        Application.Run(f);
        return 0;
    }
}
