using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

// 双窗口对照：同一个进程自己创建两个窗口，其中一个再由 shell 启动的对照进程创建。
//
// 目的：把"目标窗口是在哪个上下文里创建的"这个变量单独摘出来。
//
// 背景（2026-10-14 定案）：
//   · Codex helper 启动的进程        → 注入 3/3 可用
//   · explorer 双击启动的进程        → 注入 3/3 可用
//   · agent shell 里生出来的进程     → 注入 0/3 不可用
// 所以"目标窗口的创建上下文"很可能是决定性变量。
//
// 本程序做两件事：
//   1. 用 Win32 API 创建一个窗口（窗口 A）
//   2. 用 Process.Start 起一个 Notepad 子进程（窗口 B，父进程是本程序）
// 然后报告两个窗口的句柄，交由外部的 helper 分别尝试注入。
//
// 注意：本程序自己不做注入 —— 它只负责"造两个来自不同创建路径的目标窗口"。
// 注入由 helper 做，判据是光标是否真的移动 + 窗口是否真的收到点击。
//
// 用法：DoubleTarget
class DoubleTarget
{
    [DllImport("user32.dll")]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")]
    static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(IntPtr n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SetWindowTextW(IntPtr h, string s);

    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }

    static WndProc _keep;
    static IntPtr _winA = IntPtr.Zero;
    static int _clicksA;

    static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        // WM_LBUTTONDOWN = 0x0201
        if (m == 0x0201 && h == _winA)
        {
            _clicksA++;
            SetWindowTextW(_winA, "窗口A —— 被点击 " + _clicksA + " 次");
        }
        return DefWindowProcW(h, m, w, l);
    }

    [STAThread]
    static void Main()
    {
        string outFile = @"D:\dsh workspace\dsh-desktop-control\.state\double-target.log";
        var sb = new StringBuilder();
        Action<string> W = delegate (string s)
        {
            sb.AppendLine(s);
            Console.WriteLine(s);
        };

        W("=== 双目标测试 ===");
        W("时间: " + DateTime.Now.ToString("HH:mm:ss.fff"));
        W("本进程 pid=" + Process.GetCurrentProcess().Id);
        W("");

        // ── 窗口 A：本进程用 CreateWindowExW 直接创建
        WNDCLASS wc = new WNDCLASS();
        _keep = Proc;
        wc.lpfnWndProc = _keep;
        wc.hInstance = GetModuleHandleW(IntPtr.Zero);
        wc.lpszClassName = "DscDoubleTargetA";
        RegisterClassW(ref wc);

        _winA = CreateWindowExW(0, "DscDoubleTargetA", "窗口A",
            0x00CF0000u | 0x10000000u | 0x04000000u /*WS_CLIPSIBLINGS*/,
            80, 120, 420, 200, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        W("窗口A (本进程创建) hwnd=" + _winA);

        // ── 窗口 B：起一个记事本，父进程是本进程
        Process np = null;
        try
        {
            np = Process.Start("notepad.exe");
            W("窗口B (记事本子进程) pid=" + np.Id + "  父进程=本进程");
        }
        catch (Exception e)
        {
            W("启动记事本失败: " + e.Message);
        }

        W("");
        W("两个目标窗口已就绪。请让 helper 分别尝试点击，然后看本日志末尾的计数。");
        W("窗口A 的标题会显示被点击次数（点击真的到达才会变）。");
        W("窗口B（记事本）请让 helper 用 click_element 点它的编辑区。");
        W("");

        // 把两个窗口的句柄写到文件，并在 60 秒内持续记录窗口A的点击计数
        Action flush = delegate
        {
            try
            {
                File.WriteAllText(outFile,
                    sb.ToString() +
                    "---- 实时状态 ----" + Environment.NewLine +
                    "窗口A hwnd=" + _winA + "  被点击 " + _clicksA + " 次" + Environment.NewLine +
                    "窗口B pid=" + (np != null ? np.Id.ToString() : "?") + Environment.NewLine);
            }
            catch { }
        };
        flush();

        // 消息循环 + 定时刷新日志
        var t = new System.Windows.Forms.Timer();
        t.Interval = 1000;
        t.Tick += delegate { flush(); };
        t.Start();

        var quit = new System.Windows.Forms.Timer();
        quit.Interval = 90000;
        quit.Tick += delegate { flush(); Application.Exit(); };
        quit.Start();

        W("（90 秒后自动退出，期间每秒刷新日志）");
        flush();
        Application.Run();
        flush();
    }
}
