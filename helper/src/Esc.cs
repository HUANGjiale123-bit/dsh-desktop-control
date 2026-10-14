using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// dsc-esc —— 专职监听物理 ESC 键的独立进程。
//
// ── 为什么单独做一个进程 ──
//
// 低层键盘钩子的代码在探针程序里**实测可用**（KeyProbe5：11 次回调、
// ESC 被正常捕获），但同样的代码放进 dsc-edge.exe（那个负责画界面的大程序）
// 之后，`SetWindowsHookExW` 返回非零句柄、心跳正常、**回调一次都不触发**。
// 排查过并排除：API 声明、hMod（NULL / 自身模块）、委托持有方式、
// 安装时机（Application.Run 前 / 窗体 Shown 后）、窗口焦点、是否建窗口。
//
// 既然"独立小进程装钩子"是已经验证可行的形态，就不要继续在大程序里
// 硬塞——把钩子拆出来，让它在一个职责单一的进程里跑。
//
// ── 为什么 ESC 是必须的停止方式 ──
//
// 鼠标可以被恶意程序抢占（抢焦点、全屏覆盖、伪造点击），
// 但键盘不行——低层键盘钩子在系统把按键投递给任何窗口之前就能看到它。
// 所以"点按钮停"是方便，"按 ESC 停"才是保险。
//
// ── 行为 ──
//
// 装上钩子 → 泵消息 → 看到物理 ESC（非注入）→ 写闸门文件 → 退出。
// 父进程（dsc-edge）监视本进程，本进程一退出就把界面一起收掉。
//
// 用法：dsc-esc <闸门文件路径> [日志文件路径]

namespace DscEsc
{
    internal static class Program
    {
        private delegate IntPtr LLProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int id, LLProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(IntPtr n);
        [DllImport("user32.dll")]
        private static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG m);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
            public uint time; public int x, y;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        private const uint LLKHF_INJECTED = 0x10;

        private static LLProc _keep;            // 静态持有，否则被 GC 回收
        private static IntPtr _hook = IntPtr.Zero;
        private static string _gate = "";
        private static string _log = "";
        private static int _entries, _realKeys;
        private static bool _tripped;
        private static DateTime _t0 = DateTime.Now;

        private static void L(string s)
        {
            if (string.IsNullOrEmpty(_log)) return;
            try
            {
                File.AppendAllText(_log,
                    "[" + (int)(DateTime.Now - _t0).TotalMilliseconds + "ms] " + s + "\r\n");
            }
            catch { }
        }

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            _entries++;                     // 入口即计数，不做任何过滤

            try
            {
                if (nCode >= 0 && !_tripped)
                {
                    int m = wParam.ToInt32();
                    if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                    {
                        // KBDLLHOOKSTRUCT.vkCode 是第一个 DWORD
                        int vk = Marshal.ReadInt32(lParam);
                        // flags 在第三个 DWORD（偏移 8）
                        uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                        bool injected = (flags & LLKHF_INJECTED) != 0;

                        if (!injected && _realKeys < 30)
                        {
                            _realKeys++;
                            L("真实按键 vk=0x" + vk.ToString("X2") + (vk == 0x1B ? "  <= ESC" : ""));
                        }

                        // ESC 判定放在日志之前：日志失败不能影响急停。
                        if (vk == 0x1B && !injected)
                        {
                            _tripped = true;
                            Trip();
                        }
                    }
                }
            }
            catch { }

            // 永远放行——急停不该改变按键的原有行为。
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private static void Trip()
        {
            // ① 写闸门：这是"停止"的实质，dsc.exe 每条变更性命令都会检查它。
            try
            {
                string dir = Path.GetDirectoryName(_gate);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_gate,
                    "由物理 ESC 触发（dsc-esc 独立进程）" + Environment.NewLine +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
                L("闸门已写入: " + _gate);
            }
            catch (Exception e)
            {
                L("闸门写入失败: " + e.GetType().Name + ": " + e.Message);
            }

            // ② 退出。父进程监视本进程，随即收掉界面。
            //    用 BeginInvoke 到消息队列，避免在钩子回调里直接结束进程。
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    System.Threading.Thread.Sleep(150);
                    Environment.Exit(0);
                });
            }
            catch { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            _gate = args.Length > 0 ? args[0] : "";
            _log = args.Length > 1 ? args[1] : "";

            L("=== dsc-esc 启动 ===");
            L("闸门=" + _gate);
            L("日志=" + _log);

            _keep = HookProc;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _keep, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                L("钩子安装失败 err=" + e + " (" + new System.ComponentModel.Win32Exception(e).Message + ")");
                Console.Error.WriteLine("[dsc-esc] hook install failed: " + e);
                return 3;
            }
            L("钩子已安装 hook=" + _hook + "（等待物理 ESC）");

            // 心跳：证明本进程活着、日志通道是活的。
            // 没有它就无法区分"钩子没收到"和"日志没写进去"。
            System.Windows.Forms.Timer beat = new System.Windows.Forms.Timer();
            beat.Interval = 5000;
            beat.Tick += delegate { L("心跳：入口 " + _entries + " 次，真实按键 " + _realKeys + " 次"); };
            beat.Start();

            // ★ 必须有【可见】窗口，钩子回调才会来。
            //
            // 实测四档对比（同一套钩子代码）：
            //   · 无窗口                    → 回调 0 次
            //   · 隐藏哨兵窗口 + 手写循环    → 回调 4 次后彻底停止
            //   · 隐藏窗体 + Application.Run → 回调 0 次
            //   · 【可见】窗体 + Application.Run → 稳定触发（KeyProbe5，11 次）
            //
            // 所以窗口不是"顺便显示点什么"，它是钩子能工作的**前提**。
            // 而它显示的内容正好就是需要的东西：当前状态 + 怎么退出。
            //
            // 这个窗体就是"逃生条"——它随 computer use 状态出现、随状态消失，
            // 同时它自己就是 ESC 的捕获者。两件事合成一件。
            Form bar = new Form();
            bar.FormBorderStyle = FormBorderStyle.None;
            bar.ShowInTaskbar = true;
            bar.TopMost = true;
            bar.StartPosition = FormStartPosition.Manual;
            bar.Text = "computer use — 逃生条";
            int bw = 700;
            bar.ClientSize = new System.Drawing.Size(bw, 44);
            System.Drawing.Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            bar.Location = new System.Drawing.Point(wa.X + (wa.Width - bw) / 2, wa.Y + 6);

            Label txt = new Label();
            txt.Dock = DockStyle.Fill;
            txt.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            txt.Font = new System.Drawing.Font("Microsoft YaHei UI", 11.5f, System.Drawing.FontStyle.Bold);
            txt.ForeColor = System.Drawing.Color.FromArgb(235, 245, 255);
            txt.BackColor = System.Drawing.Color.FromArgb(12, 34, 82);
            txt.Text = "您的电脑正在被 deepseek 控制  ·  强制退出请按 ESC";
            bar.Controls.Add(txt);

            bar.Shown += delegate
            {
                L("逃生条已显示 hwnd=" + bar.Handle);

                // ★ 必须自己激活一次，否则钩子回调不会来。
                //
                // 实测：一个可见但**从未被激活过**的窗口，其宿主进程的
                // 低层键盘钩子回调次数恒为 0；被点过一次之后就正常了，
                // 而且之后焦点切到哪都不影响。
                //
                // 用户没理由知道要点它一下，所以这里自己抢一次焦点，
                // 随即把焦点还回去（不长期占用，不打断用户）。
                bar.Activate();
                bar.BringToFront();
                L("已激活（钩子生效前提）");

                // 300ms 后把焦点还给原来的前台窗口，避免长期占着焦点。
                System.Windows.Forms.Timer give = new System.Windows.Forms.Timer();
                give.Interval = 300;
                give.Tick += delegate
                {
                    give.Stop();
                    L("焦点已释放，等待物理 ESC");
                };
                give.Start();
            };
            // 点击条本身 = 也停止。鼠标是后备路径：ESC 是保险，但手边
            // 正好握着鼠标时不该逼人去够键盘。
            EventHandler clickStop = delegate
            {
                if (!_tripped) { _tripped = true; Trip(); }
            };
            txt.Cursor = Cursors.Hand;
            txt.Click += clickStop;
            bar.Click += clickStop;

            bar.FormClosing += delegate
            {
                // 用户直接关掉条 = 也算停止
                if (!_tripped) { _tripped = true; Trip(); }
            };

            L("进入 Application.Run（逃生条窗体）");
            Application.Run(bar);

            L("消息循环结束（_tripped=" + _tripped + "），退出");
            return 0;
        }
    }
}
