using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 测 RegisterHotKey 在本环境能不能用。
//
// 背景：低层键盘钩子（SetWindowsHookEx WH_KEYBOARD_LL）在这台机器上
// **装得上但不触发** —— 句柄非零、消息循环正常、回调一次都不来。
// 所以"按 ESC 停"这条路上一次断了。
//
// RegisterHotKey 是**另一套机制**：它向系统注册一个组合键，
// 由系统在按下时投递 WM_HOTKEY 消息，不依赖钩子链、不需要注入。
// 这条路今天没测过，值得一试——如果它工作，"按 ESC 停"就能兑现。
//
// 判据：注册返回值 + 等待期内是否真的收到 WM_HOTKEY + 用户是否按了键。
// 用法：HotkeyProbe <秒数> [输出文件]
class HotkeyProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    static extern int GetLastError();

    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    sealed class ProbeForm : Form
    {
        public int Received;
        public string LastInfo = "";

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                Received++;
                int id = m.WParam.ToInt32();
                LastInfo = "id=" + id + " 修饰=" + (m.LParam.ToInt32() & 0xFFFF);
            }
            base.WndProc(ref m);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 20;
        string outFile = args.Length > 1 ? args[1] : null;

        var lines = new System.Text.StringBuilder();
        Action<string> W = delegate (string s)
        {
            Console.WriteLine(s);
            lines.AppendLine(s);
            if (outFile != null) { try { System.IO.File.WriteAllText(outFile, lines.ToString()); } catch { } }
        };

        W("=== RegisterHotKey 探针 ===");
        W("时间: " + DateTime.Now.ToString("HH:mm:ss"));
        W("");

        Application.EnableVisualStyles();
        var f = new ProbeForm();
        f.ShowInTaskbar = false;
        f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
        f.Text = "HotkeyProbe — 请按 ESC";
        f.StartPosition = FormStartPosition.CenterScreen;
        f.ClientSize = new System.Drawing.Size(360, 90);

        var lbl = new Label();
        lbl.Dock = DockStyle.Fill;
        lbl.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        lbl.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f);
        lbl.Text = "请按几次 ESC 键\n（" + secs + " 秒后自动结束）";
        f.Controls.Add(lbl);

        f.Shown += delegate
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = secs * 1000;
            t.Tick += delegate
            {
                t.Stop();
                W("");
                W("=== 结果 ===");
                W("注册是否成功: " + (f.Tag != null ? f.Tag.ToString() : "未尝试"));
                W("收到的 WM_HOTKEY 次数: " + f.Received);
                if (f.Received > 0) W("最后一次: " + f.LastInfo);
                W("");
                W(f.Received > 0
                    ? "结论：RegisterHotKey **可用** —— 按 ESC 停这条路能兑现"
                    : "结论：RegisterHotKey 注册成功但**收不到消息** —— 按 ESC 停不可靠");
                f.Close();
            };
            t.Start();

            // 注册 ESC（无修饰键）
            bool ok = RegisterHotKey(f.Handle, 1, MOD_NOREPEAT, 0x1B /*VK_ESCAPE*/);
            int err = Marshal.GetLastWin32Error();
            f.Tag = ok ? "成功" : ("失败 err=" + err + " (" + new System.ComponentModel.Win32Exception(err).Message + ")");
            W("RegisterHotKey(VK_ESCAPE) → " + f.Tag);

            // 再试一个带修饰键的，排除"单独 ESC 被系统占用"的可能
            bool ok2 = RegisterHotKey(f.Handle, 2, MOD_NOREPEAT | 0x0002 /*MOD_CONTROL*/, 0x1B);
            int err2 = Marshal.GetLastWin32Error();
            W("RegisterHotKey(Ctrl+ESC) → " + (ok2 ? "成功" : ("失败 err=" + err2)));
            W("");
            W("请现在按 ESC（以及 Ctrl+ESC），各按几次。");
        };

        f.FormClosed += delegate
        {
            UnregisterHotKey(f.Handle, 1);
            UnregisterHotKey(f.Handle, 2);
            Application.ExitThread();
        };

        Application.Run(f);
        return 0;
    }
}
