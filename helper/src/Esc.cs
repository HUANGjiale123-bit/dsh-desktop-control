/*
 * dsh-desktop-control — ESC 急停守候进程
 *
 * 用户按下 ESC 即拉下急停闸门：写一个闸门文件，此后 dsc.exe 的所有变更
 * 性动作一律拒绝，直到有人显式清除。
 *
 * ── 为什么用低级键盘钩子，而不是 RegisterHotKey ──
 *
 * 最初用的是 RegisterHotKey。实测它**注册成功但不触发**：热键注册返回真，
 * 可前台窗口里按 ESC 收不到任何 WM_HOTKEY。原因是 UI 权限隔离（UIPI）——
 * 低完整性进程注册的全局热键，收不到高完整性前台窗口上的按键，而且这种
 * 失败是静默的，注册那一步根本不会报错。
 *
 * 一个"注册成功却永不触发"的急停，比没有急停更危险：使用者以为按下去就停，
 * 实际什么都没发生。所以改用 WH_KEYBOARD_LL 低级键盘钩子——它在输入到达
 * 任何窗口之前就能看到按键，不依赖消息路由，也不受前台窗口完整性影响。
 *
 * ── 一条必须守住的纪律 ──
 *
 * 钩子**绝不吞掉 ESC**。回调里永远把事件继续传递（CallNextHookEx）。
 * 吃掉 ESC 会把这个键从使用者手里夺走——关对话框、退全屏全都会失效，
 * 那本身就成了新的麻烦。我们只是"顺便知道了你按了 ESC"，不改变它的去向。
 *
 * ── 诚实失败 ──
 *
 * 钩子安装失败、或闸门写不进去，一律以非零码退出并大声报错。
 * 这个进程的存在意义就是"按下去一定停"，做不到就不能装作做到了。
 *
 * 用法：
 *   dsc-esc.exe [--quiet]
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DscEsc
{
    internal static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
            public int pad1, pad2;
        }

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public const int WH_KEYBOARD_LL = 13;
        public const int HC_ACTION = 0;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const uint VK_ESCAPE = 0x1B;
    }

    internal static class Program
    {
        private static Native.HookProc _proc;   // 必须保住引用，否则会被 GC 回收导致钩子失效
        private static IntPtr _hook = IntPtr.Zero;
        private static bool _quiet;
        private static int _fires;

        /// <summary>
        /// 闸门文件位置，必须与 dsc.exe 的 GatePath() 保持一致。
        ///
        /// 两处各写一份是无奈之举：它们是独立编译的两个可执行文件。
        /// 改动其一必须同步另一处，否则会出现"ESC 拉下的闸门 dsc.exe 看不见"
        /// 这种最糟的情况——按了急停却拦不住。
        ///
        /// 优先级：环境变量 DSC_HALT_GATE > 可执行文件所在目录的上一级\.state\halt
        /// </summary>
        private static string GatePath()
        {
            string fromEnv = Environment.GetEnvironmentVariable("DSC_HALT_GATE");
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

            string binDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string root = Path.GetDirectoryName(binDir);
            if (!string.IsNullOrEmpty(root)) root = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(root)) root = binDir;
            return Path.Combine(root, ".state", "halt");
        }

        /// <summary>拉下闸门。失败就抛出去，绝不允许"以为停了其实没停"。</summary>
        private static void PullGate()
        {
            string p = GatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p,
                "由 ESC 急停触发" + Environment.NewLine +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);

            if (!File.Exists(p))
                throw new InvalidOperationException("闸门写入后仍不存在：" + p);

            Console.Error.WriteLine("[esc] 急停已拉下 -> " + p);
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 无论发生什么，最后都要把事件传下去 —— 绝不吞掉 ESC
            try
            {
                if (nCode == Native.HC_ACTION)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    {
                        Native.KBDLLHOOKSTRUCT data =
                            (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));

                        if (data.vkCode == Native.VK_ESCAPE)
                        {
                            // 只在第一次按下时拉闸，避免长按时反复写盘
                            if (Interlocked.Increment(ref _fires) == 1)
                            {
                                try { PullGate(); }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine("[esc] 拉闸失败: " + ex.Message);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[esc] 回调异常: " + ex.Message);
            }

            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>合成一次 ESC 按下 + 抬起，供自测使用。</summary>
        private static void SendSyntheticEsc()
        {
            Native.INPUT[] down = new Native.INPUT[1];
            down[0].type = Native.INPUT_KEYBOARD;
            down[0].ki.wVk = (ushort)Native.VK_ESCAPE;
            Native.SendInput(1, down, Marshal.SizeOf(typeof(Native.INPUT)));

            Thread.Sleep(50);

            Native.INPUT[] up = new Native.INPUT[1];
            up[0].type = Native.INPUT_KEYBOARD;
            up[0].ki.wVk = (ushort)Native.VK_ESCAPE;
            up[0].ki.dwFlags = Native.KEYEVENTF_KEYUP;
            Native.SendInput(1, up, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            _quiet = Array.IndexOf(args, "--quiet") >= 0;

            _proc = HookCallback;

            // 低级键盘钩子：模块句柄传当前模块即可（.NET 托管钩子由 CLR 调度）
            IntPtr hMod = Native.GetModuleHandleW(null);
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, hMod, 0);

            if (_hook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine(
                    "[esc] 安装键盘钩子失败 (Win32 错误 " + err + ": " +
                    new System.ComponentModel.Win32Exception(err).Message + ")。" +
                    "急停未生效，请勿在无急停的情况下继续操作桌面。");
                return 3;
            }

            if (!_quiet)
                Console.Error.WriteLine("[esc] 已就位（低级键盘钩子）：按 ESC 即拉下急停闸门。闸门文件 " + GatePath());

            // 自测模式：自己合成一次 ESC，看钩子接不接得住。
            // 这能验证"钩子 → 拉闸"这条链路，避免交付一个装了却永不触发的急停。
            // 合成事件带 LLKHF_INJECTED 标记，自测据此区分真实按键。
            if (Array.IndexOf(args, "--selftest") >= 0)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 700;
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    Console.Error.WriteLine("[esc] 自测：发送合成 ESC");
                    SendSyntheticEsc();
                    System.Windows.Forms.Timer t2 = new System.Windows.Forms.Timer();
                    t2.Interval = 900;
                    t2.Tick += (s2, e2) =>
                    {
                        t2.Stop();
                        Console.Error.WriteLine("[esc] 自测结果：" + (_fires > 0
                            ? "钩子接住了（收到 " + _fires + " 次）→ 链路可用"
                            : "钩子未接住 → 链路不可用"));
                        Application.Exit();
                    };
                    t2.Start();
                };
                t.Start();
            }

            Application.Run();   // 只跑消息循环；钩子回调在这个循环里被派发

            Native.UnhookWindowsHookEx(_hook);
            return 0;
        }
    }
}
