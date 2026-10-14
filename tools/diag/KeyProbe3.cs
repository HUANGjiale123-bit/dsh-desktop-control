using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 键盘可达性探针（诊断加固版）。
//
// 前几版失败的教训：
//   1. 结果只打 stdout —— 这是 winexe，从 shell 后台启动时输出会丢。
//   2. 自定义了一个叫 Show() 的方法，**覆盖了 Form.Show()**，窗口显示不出来，
//      Application.Run 立刻返回，程序秒退，什么也没留下。
//   3. 没有任何顶层异常捕获，所以"秒退"是个哑谜。
//
// 这一版把这三条都堵上：
//   · 所有输出写文件，每一步都落盘
//   · 不定义任何与基类同名的方法
//   · Main 全程 try/catch，异常连堆栈一起写文件
//
// 用法：KeyProbe3 <秒数> <输出文件>
class KeyProbe3
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

    private static int _hookEvents;
    private static LLProc _keepAlive;
    private static string _file = @"D:\keyprobe3.log";
    private static readonly object _lk = new object();
    private static DateTime _t0 = DateTime.Now;

    private static void L(string s)
    {
        lock (_lk)
        {
            try
            {
                File.AppendAllText(_file,
                    "[" + (int)(DateTime.Now - _t0).TotalMilliseconds + "ms] " + s + "\r\n");
            }
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
                if (_hookEvents <= 50)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    L("钩子回调 #" + _hookEvents + " vk=0x" + vk.ToString("X2") +
                      (vk == 0x1B ? "  <== ESC" : ""));
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private sealed class F : Form
    {
        public int KeyDown, SysKeyDown, Hotkey;
        public IntPtr Hook = IntPtr.Zero;
        public string HookNote = "未装", HotkeyNote = "未注册";
        private Label _lbl;
        private System.Windows.Forms.Timer _ui;

        public F()
        {
            Text = "键盘探针 — 请按 A / 空格 / ESC";
            ClientSize = new System.Drawing.Size(600, 240);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            KeyPreview = true;
            _lbl = new Label();
            _lbl.Dock = DockStyle.Fill;
            _lbl.Font = new System.Drawing.Font("Consolas", 11f);
            _lbl.Padding = new Padding(12);
            Controls.Add(_lbl);
            _ui = new System.Windows.Forms.Timer { Interval = 150 };
            _ui.Tick += delegate { Redraw(); };
        }

        public void Redraw()
        {
            _lbl.Text =
                "【窗口消息】WM_KEYDOWN = " + KeyDown + "   WM_SYSKEYDOWN = " + SysKeyDown + "\r\n" +
                "【热键】" + HotkeyNote + "   WM_HOTKEY = " + Hotkey + "\r\n" +
                "【低层钩子】" + HookNote + "   回调次数 = " + _hookEvents + "\r\n" +
                "\r\n（钩子是全局的，不需要这个窗口有焦点）";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_KEYDOWN) { KeyDown++; L("窗口 WM_KEYDOWN vk=0x" + m.WParam.ToInt32().ToString("X2")); }
            else if (m.Msg == WM_SYSKEYDOWN) { SysKeyDown++; L("窗口 WM_SYSKEYDOWN vk=0x" + m.WParam.ToInt32().ToString("X2")); }
            else if (m.Msg == WM_HOTKEY) { Hotkey++; L("*** 收到 WM_HOTKEY ***"); }
            base.WndProc(ref m);
        }

        public void Setup()
        {
            _keepAlive = HookProc;
            Hook = SetWindowsHookExW(WH_KEYBOARD_LL, _keepAlive, GetModuleHandleW(IntPtr.Zero), 0);
            if (Hook == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                HookNote = "装不上 err=" + e;
                L("钩子安装失败 err=" + e);
            }
            else { HookNote = "已装上"; L("钩子安装成功"); }

            if (RegisterHotKey(Handle, 1, 0x4000, 0x1B)) { HotkeyNote = "成功"; L("RegisterHotKey(ESC) 成功"); }
            else { int e = Marshal.GetLastWin32Error(); HotkeyNote = "失败 err=" + e; L("RegisterHotKey 失败 err=" + e); }
        }

        public void Teardown()
        {
            if (Hook != IntPtr.Zero) UnhookWindowsHookEx(Hook);
            UnregisterHotKey(Handle, 1);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 1) _file = args[1];
        int secs = args.Length > 0 ? int.Parse(args[0]) : 60;

        try
        {
            try { if (File.Exists(_file)) File.Delete(_file); } catch { }
            L("=== 启动 ===");
            L("参数: 时长=" + secs + "秒  日志=" + _file);
            L("进程 pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);

            Application.EnableVisualStyles();
            L("EnableVisualStyles 完成");

            F f = new F();
            L("窗体已构造");

            bool shown = false, closed = false;

            f.Shown += delegate
            {
                shown = true;
                L("Shown 事件触发");
                f.Setup();
                f.Redraw();
                var t = new System.Windows.Forms.Timer { Interval = secs * 1000 };
                t.Tick += delegate
                {
                    t.Stop();
                    L("");
                    L("=== 汇总 ===");
                    L("钩子回调次数 = " + _hookEvents);
                    L("WM_KEYDOWN   = " + f.KeyDown);
                    L("WM_SYSKEYDOWN= " + f.SysKeyDown);
                    L("WM_HOTKEY    = " + f.Hotkey);
                    L("钩子=" + f.HookNote + "  热键=" + f.HotkeyNote);
                    L(_hookEvents > 0
                        ? "结论：钩子可用 <== ESC 全局捕获可行"
                        : "结论：钩子装上但一次都没触发");
                    f.Teardown();
                    f.Close();
                };
                t.Start();
            };

            f.FormClosed += delegate { closed = true; L("窗体已关闭"); };

            L("调用 Application.Run ...");
            Application.Run(f);
            L("Application.Run 返回。shown=" + shown + " closed=" + closed);
            if (!shown) L("!! 窗体从未显示 —— 这就是程序秒退的原因");
            return 0;
        }
        catch (Exception e)
        {
            L("!! 顶层异常: " + e.GetType().Name + ": " + e.Message);
            L(e.StackTrace ?? "");
            try
            {
                File.AppendAllText(@"D:\keyprobe3-crash.txt",
                    DateTime.Now + " " + e + "\r\n\r\n");
            }
            catch { }
            return 1;
        }
    }
}
