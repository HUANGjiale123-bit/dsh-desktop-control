/*
 * dsc-esc-probe —— 按键读取能力探测
 *
 * 目的：确定本进程能否观察到真实键盘输入，以及哪条通道可用。
 *
 * 为什么需要它：本项目的 ESC 急停此前三次都失败，但那三次全部是在
 * **sandbox 启动的进程**里测的——而那个上下文后来被证明"沙箱里不行"不能
 * 推广成"这台机器不行"（鼠标注入就是这么被误判了一整轮）。所以"ESC 收不到"
 * 这个结论同样不可靠，需要在一个**能代表宿主**的上下文里重测。
 *
 * 三种机制，各自独立，逐个报告：
 *   1. GetAsyncKeyState  轮询按键状态（最弱、最不可能被拦的通道）
 *   2. GetKeyState       读调用线程消息队列的按键状态
 *   3. RegisterHotKey    注册系统级热键，靠消息队列收 WM_HOTKEY
 *
 * 刻意**不装 WH_KEYBOARD_LL 低级钩子**：钩子若处理不当会卡住全局键盘输入，
 * 而这是一个诊断工具，不值得冒那个风险。先看这三条通不通；三条都不通，
 * 说明读键盘这件事在本上下文根本不可用，装钩子也不会更好。
 *
 * 用法：
 *   dsc-esc-probe [秒数]        默认 20 秒
 *
 * 输出：每次观察到按键的即时日志（带机制与时标），结束时给汇总。
 * 按 ESC 会立即报告；其它键也会报告，用来确认"能读到键"而不是"恰好读到 ESC"。
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DscEscProbe
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_F12 = 0x7B;
        public const uint PM_REMOVE = 0x0001;
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            int seconds = args.Length > 0 ? int.Parse(args[0]) : 20;
            long deadline = Environment.TickCount + seconds * 1000;

            Console.WriteLine("=== 按键读取能力探测 ===");
            Console.WriteLine("时长: " + seconds + " 秒");
            Console.WriteLine("前台窗口: \"" + ForegroundTitle() + "\" (hwnd=" + Native.GetForegroundWindow() + ")");
            Console.WriteLine();

            // ── 机制 3：RegisterHotKey
            bool hotEsc = Native.RegisterHotKey(IntPtr.Zero, 1, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
            int hotErr = hotEsc ? 0 : Marshal.GetLastWin32Error();
            bool hotF12 = Native.RegisterHotKey(IntPtr.Zero, 2, Native.MOD_NOREPEAT, Native.VK_F12);
            int hotF12Err = hotF12 ? 0 : Marshal.GetLastWin32Error();
            Console.WriteLine("[机制3] RegisterHotKey(ESC)  = " + (hotEsc ? "成功" : "失败 Win32=" + hotErr));
            Console.WriteLine("[机制3] RegisterHotKey(F12)  = " + (hotF12 ? "成功" : "失败 Win32=" + hotF12Err));
            Console.WriteLine();

            Console.WriteLine("现在请按 ESC（如果没有反应，再按几次）。其它键也会被记录。");
            Console.WriteLine("─────────────────────────────────────────────");

            int escDownCount = 0, otherKeyCount = 0;
            bool escSeenAsync = false, escSeenKeyState = false, escSeenHotkey = false;
            HashSet<int> otherKeys = new HashSet<int>();
            Dictionary<int, byte> lastState = new Dictionary<int, byte>();

            // 监视范围：ESC + 字母数字 + F 键 + 方向键，覆盖面够宽
            int[] watch = new int[]
            {
                Native.VK_ESCAPE,
                0x41,0x42,0x43,0x44,0x45,0x46,0x47,0x48,0x49,0x4A,0x4B,0x4C,0x4D,
                0x4E,0x4F,0x50,0x51,0x52,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,
                0x20, 0x0D, 0x09, 0x08, 0x10, 0x11, 0x12,
                0x25,0x26,0x27,0x28,
                0x70,0x71,0x72,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x7B
            };

            while (Environment.TickCount < deadline)
            {
                // ── 机制 1：GetAsyncKeyState
                foreach (int vk in watch)
                {
                    short st = Native.GetAsyncKeyState(vk);
                    bool down = (st & 0x8000) != 0;
                    byte prev;
                    if (lastState.TryGetValue(vk, out prev))
                    {
                        if (down && prev == 0)
                        {
                            if (vk == Native.VK_ESCAPE)
                            {
                                escSeenAsync = true;
                                escDownCount++;
                                Console.WriteLine("  [GetAsyncKeyState] ESC 按下  (raw=0x" + ((ushort)st).ToString("X4") + ")");
                            }
                            else
                            {
                                otherKeyCount++;
                                otherKeys.Add(vk);
                            }
                        }
                    }
                    lastState[vk] = down ? (byte)1 : (byte)0;
                }

                // ── 机制 2：GetKeyState（走调用线程的消息队列状态）
                short ks = Native.GetKeyState(Native.VK_ESCAPE);
                if ((ks & 0x8000) != 0 && !escSeenKeyState)
                {
                    escSeenKeyState = true;
                    Console.WriteLine("  [GetKeyState]      ESC 按下  (raw=0x" + ((ushort)ks).ToString("X4") + ")");
                }

                // ── 机制 3：热键消息
                Native.MSG msg;
                while (Native.PeekMessageW(out msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
                {
                    if (msg.message == Native.WM_HOTKEY)
                    {
                        int id = msg.wParam.ToInt32();
                        if (id == 1) { escSeenHotkey = true; Console.WriteLine("  [RegisterHotKey]   ESC 热键触发"); }
                        else Console.WriteLine("  [RegisterHotKey]   F12 热键触发（说明热键机制本身可用）");
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessageW(ref msg);
                }

                Thread.Sleep(15);
            }

            if (hotEsc) Native.UnregisterHotKey(IntPtr.Zero, 1);
            if (hotF12) Native.UnregisterHotKey(IntPtr.Zero, 2);

            Console.WriteLine("─────────────────────────────────────────────");
            Console.WriteLine("=== 汇总 ===");
            // 拆开写：嵌套三元夹在字符串拼接里，C# 5 的优先级会解析失败
            string asyncNote;
            if (!escSeenAsync) asyncNote = "否";
            else if (escDownCount > 1) asyncNote = "是（" + escDownCount + " 次）";
            else asyncNote = "是";
            Console.WriteLine("GetAsyncKeyState 读到 ESC : " + asyncNote);
            Console.WriteLine("GetKeyState      读到 ESC : " + (escSeenKeyState ? "是" : "否"));
            Console.WriteLine("RegisterHotKey   收到 ESC : " + (escSeenHotkey ? "是" : "否"));
            Console.WriteLine("读到的其它按键数          : " + otherKeyCount +
                (otherKeyCount > 0 ? "（vk: " + JoinVks(otherKeys) + "）" : ""));
            Console.WriteLine();
            bool any = escSeenAsync || escSeenKeyState || escSeenHotkey;
            if (any)
                Console.WriteLine("结论: ESC 在本上下文可读 —— 急停可以做，用可用的那条通道。");
            else if (otherKeyCount > 0)
                Console.WriteLine("结论: 能读到键盘（其它键有反应），但 ESC 没读到 —— 可能 ESC 被别的东西吃掉了。");
            else
                Console.WriteLine("结论: 一条通道都不通，连其它键也读不到 —— 本上下文确实观察不到真实输入。");
            return any ? 0 : 1;
        }

        private static string JoinVks(HashSet<int> vks)
        {
            List<string> parts = new List<string>();
            foreach (int vk in vks)
            {
                if (vk >= 0x41 && vk <= 0x5A) parts.Add(((char)vk).ToString());
                else parts.Add("0x" + vk.ToString("X2"));
            }
            return string.Join(",", parts.ToArray());
        }

        private static string ForegroundTitle()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return "(无)";
            StringBuilder sb = new StringBuilder(256);
            Native.GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
