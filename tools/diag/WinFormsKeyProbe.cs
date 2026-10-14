using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

// 注入能力探针（WinForms 版）—— 判据是"文本框读回的内容"，不依赖光标
//
// 为什么不依赖光标：光标由用户的物理鼠标控制。用户一动鼠标，
// "注入后光标在哪"这个判据就失效。本项目已经因此得到过一组互相矛盾的
// 数据（同一路径先 3/3、后 0/3），无法判断哪几轮是干净的。
//
// 为什么用 WinForms 而不是自建 Win32 窗口：自建窗口实测收不到 WM_CHAR
// （`KeyProbe` 的 received 一直是空），而 WinForms TextBox 是验证过能收到的。
//
// 判据链：SendInput 注入 → TextBox.Text 读回 → 与 payload 比对。
// 用户的鼠标怎么动都不影响这个判据。
//
// 用法：WinFormsKeyProbe <输出文件> [等待秒数]
class WinFormsKeyProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] i, int cb);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")]
    static extern IntPtr GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint a, uint b, bool f);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_KEYUP = 0x0002;

    static void InjectChar(char c)
    {
        INPUT[] pair = new INPUT[2];
        pair[0].type = INPUT_KEYBOARD;
        pair[0].ki.wScan = (ushort)c;
        pair[0].ki.dwFlags = KEYEVENTF_UNICODE;
        pair[1].type = INPUT_KEYBOARD;
        pair[1].ki.wScan = (ushort)c;
        pair[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
    }

    static string _outFile = @"D:\winkeyprobe.log";

    [STAThread]
    static void Main(string[] args)
    {
        _outFile = args.Length > 0 ? args[0] : @"D:\winkeyprobe.log";
        int waitMs = args.Length > 1 ? int.Parse(args[1]) * 1000 : 2000;

        var R = new StringBuilder();
        Action<string, string> KV = (k, v) => R.AppendLine(k + "=" + v);

        string self = "";
        try { self = Application.ExecutablePath; } catch { }
        KV("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        KV("pid", GetCurrentProcessId().ToString());
        KV("exeDir", string.IsNullOrEmpty(self) ? "?" : Path.GetDirectoryName(self));
        KV("inWorkspace",
            self.IndexOf(@"\dsh workspace\", StringComparison.OrdinalIgnoreCase) >= 0 ? "YES" : "no");
        try
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var fs = File.OpenRead(self))
            {
                byte[] h = md5.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                KV("exeMd5", sb.ToString());
            }
        }
        catch { KV("exeMd5", "unavailable"); }

        bool aware = SetProcessDPIAware();
        Thread.Sleep(100);
        KV("dpiAware", aware ? "ok" : "fail");
        KV("screen", GetSystemMetrics(0) + "x" + GetSystemMetrics(1));
        POINT c;
        GetCursorPos(out c);
        KV("cursorAtStart", c.X + "," + c.Y);

        Application.EnableVisualStyles();
        Form f = new Form();
        f.Text = "WinFormsKeyProbe";
        f.ClientSize = new Size(420, 90);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(80, 120);
        f.ShowInTaskbar = false;

        TextBox box = new TextBox();
        box.Name = "probeBox";
        box.Font = new Font("Consolas", 14);
        box.Text = "";
        box.SetBounds(10, 14, 400, 30);
        f.Controls.Add(box);

        f.Shown += delegate
        {
            box.Focus();
            f.Activate();
            Thread t = new Thread(delegate ()
            {
                Thread.Sleep(500);

                // 强制抢焦点：后台进程直接 SetForegroundWindow 会被系统拒绝，
                // 需要先 AttachThreadInput 附加到当前前台线程。
                for (int attempt = 0; attempt < 5 && GetForegroundWindow() != f.Handle; attempt++)
                {
                    IntPtr fg = GetForegroundWindow();
                    uint fgT = fg == IntPtr.Zero ? 0 : (uint)GetWindowThreadProcessId(fg, IntPtr.Zero);
                    uint myT = GetCurrentThreadId();
                    bool att = (fgT != 0 && fgT != myT) && AttachThreadInput(myT, fgT, true);
                    try
                    {
                        f.Activate();
                        BringWindowToTop(f.Handle);
                        SetForegroundWindow(f.Handle);
                        f.BeginInvoke(new Action(delegate { box.Focus(); box.SelectAll(); }));
                    }
                    finally { if (att) AttachThreadInput(myT, fgT, false); }
                    Thread.Sleep(300);
                }

                KV("foregroundIsSelf", (GetForegroundWindow() == f.Handle) ? "YES" : "NO");
                KV("boxFocused", box.Focused ? "YES" : "NO");

                if (GetForegroundWindow() != f.Handle)
                {
                    KV("RESULT", "PROBE_SETUP_FAILED_NO_FOCUS");
                    try { File.WriteAllText(_outFile, R.ToString()); } catch { }
                    Console.Write(R.ToString());
                    f.BeginInvoke(new Action(delegate { f.Close(); }));
                    return;
                }

                string payload = "KP7XZ";
                foreach (char ch in payload) { InjectChar(ch); Thread.Sleep(70); }
                Thread.Sleep(waitMs);

                string got = box.Text;
                KV("payload", payload);
                KV("received", got);
                KV("RESULT", got == payload ? "KEYBOARD_INJECTION_WORKS" : "KEYBOARD_INJECTION_DEAD");

                try { File.WriteAllText(_outFile, R.ToString()); } catch { }
                Console.Write(R.ToString());
                f.BeginInvoke(new Action(delegate { f.Close(); }));
            });
            t.IsBackground = true;
            t.Start();
        };

        Application.Run(f);
    }
}
