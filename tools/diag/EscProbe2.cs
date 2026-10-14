/*
 * dsc-esc-probe2 —— 精确到毫秒的按键通道对比
 *
 * 起因：第一版探测出现三个机制结果不一致——
 *     GetAsyncKeyState  没读到 ESC
 *     GetKeyState       读到了 (0xFF81)
 *     RegisterHotKey    注册成功但从不触发
 * 这三者读的是不同层面的东西，混在一起测分不清根因。本版把它们**逐项隔离**：
 *
 *   实验 1  GetAsyncKeyState 密集轮询（1ms 间隔），记录首次读到的时刻
 *   实验 2  GetKeyState 同样轮询，记录首次读到的时刻（对比两者时间差）
 *   实验 3  RegisterHotKey 装在**真正的 message-only 窗口**上，用 GetMessage 消息泵，
 *           而不是挂在 NULL 上靠 PeekMessage 轮询线程队列
 *   实验 4  对照：轮询其它键（空格/字母），确认"能读到键盘"这件事本身是否成立
 *
 * 为什么实验 3 要改成窗口：RegisterHotKey(hwnd=NULL) 把 WM_HOTKEY 投到线程队列，
 * 靠 PeekMessage 轮询容易漏；挂在窗口上由 DefWindowProc 处理是标准做法。
 *
 * 输出全部带毫秒时标，便于对照。
 *
 * 用法：dsc-esc-probe2 [秒数]   默认 30
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DscEscProbe2
{
    internal static class N
    {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern short GetKeyState(int vKey);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowExW(
            uint ex, string cls, string name, uint style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern ushort RegisterClassW(ref WNDCLASS wc);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(IntPtr n);
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);

        public delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        public struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
            public uint time; public int x; public int y;
        }

        public const int VK_ESCAPE = 0x1B;
        public const int VK_SPACE = 0x20;
        public const uint WM_HOTKEY = 0x0312;
        public const uint WM_CLOSE = 0x0010;
        public const uint MOD_NOREPEAT = 0x4000;
        // HWND_MESSAGE = (HWND)-3。用 long 再转 IntPtr，避免 ulong→uint 的编译错误。
        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
    }

    internal sealed class Program
    {
        private static long _t0;
        private static volatile bool _hotkeyFired;
        private static volatile int _hotkeyId;
        private static IntPtr _hwnd = IntPtr.Zero;

        // 必须用静态字段持有这个委托。只写 wc.lpfnWndProc = Proc 的话，委托
        // 仅被结构体临时引用，CreateWindowExW 调用前就可能被 GC 回收，窗口创建
        // 于是失败返回 0——上一版正是这样，热键实际挂在了 NULL 上，等于没测到窗口泵。
        private static N.WndProc _procKeepAlive;

        private static long Ms() { return Environment.TickCount - _t0; }

        private static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (m == N.WM_HOTKEY)
            {
                _hotkeyFired = true;
                _hotkeyId = w.ToInt32();
                Console.WriteLine("[" + Ms() + "ms] [实验3] WM_HOTKEY 收到! id=" + _hotkeyId);
                return IntPtr.Zero;
            }
            return N.DefWindowProcW(h, m, w, l);
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            _t0 = Environment.TickCount;
            int seconds = args.Length > 0 ? int.Parse(args[0]) : 30;

            Console.WriteLine("=== 按键通道精确对比 (pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + ") ===");
            Console.WriteLine("时长 " + seconds + " 秒。请交替按 ESC 和 空格。");
            Console.WriteLine();

            // 建一个 message-only 窗口，把热键挂在它上面
            N.WNDCLASS wc = new N.WNDCLASS();
            _procKeepAlive = Proc;   // 静态持有，防止 GC 回收
            wc.lpfnWndProc = _procKeepAlive;
            wc.hInstance = N.GetModuleHandleW(IntPtr.Zero);
            wc.lpszClassName = "DscEscProbe2Wnd";
            ushort atom = N.RegisterClassW(ref wc);
            _hwnd = N.CreateWindowExW(0, "DscEscProbe2Wnd", "probe2", 0, 0, 0, 0, 0,
                                      N.HWND_MESSAGE, IntPtr.Zero,
                                      wc.hInstance, IntPtr.Zero);
            Console.WriteLine("[实验3] message-only 窗口 hwnd = " + _hwnd + " (RegisterClass atom=" + atom + ")");

            bool hot = N.RegisterHotKey(_hwnd, 1, N.MOD_NOREPEAT, N.VK_ESCAPE);
            int err = hot ? 0 : Marshal.GetLastWin32Error();
            Console.WriteLine("[实验3] RegisterHotKey(ESC) 挂在窗口上 = " + (hot ? "成功" : "失败 Win32=" + err));
            Console.WriteLine();

            bool escAsync = false, escKeyState = false, spaceAsync = false, spaceKeyState = false;
            long escAsyncMs = -1, escKeyStateMs = -1, spaceAsyncMs = -1, spaceKeyStateMs = -1;
            int escAsyncHit = 0, escKsHit = 0, spaceAsyncHit = 0;

            byte prevEscA = 0, prevEscK = 0, prevSpaceA = 0;

            long deadline = _t0 + seconds * 1000L;
            long lastMsgPump = 0;

            while (Environment.TickCount < deadline)
            {
                // 实验 1：GetAsyncKeyState
                short ea = N.GetAsyncKeyState(N.VK_ESCAPE);
                byte eaDown = (byte)(((ea & 0x8000) != 0) ? 1 : 0);
                if (eaDown == 1 && prevEscA == 0)
                {
                    escAsyncHit++;
                    if (!escAsync) { escAsync = true; escAsyncMs = Ms(); }
                    Console.WriteLine("[" + Ms() + "ms] [实验1 GetAsyncKeyState] ESC 按下 raw=0x" + ((ushort)ea).ToString("X4"));
                }
                prevEscA = eaDown;

                short sa = N.GetAsyncKeyState(N.VK_SPACE);
                if ((sa & 0x8000) != 0 && prevSpaceA == 0)
                {
                    spaceAsyncHit++;
                    if (!spaceAsync) { spaceAsync = true; spaceAsyncMs = Ms(); }
                    Console.WriteLine("[" + Ms() + "ms] [实验1 GetAsyncKeyState] 空格按下 raw=0x" + ((ushort)sa).ToString("X4"));
                }
                prevSpaceA = (byte)(((sa & 0x8000) != 0) ? 1 : 0);

                // 实验 2：GetKeyState
                short ek = N.GetKeyState(N.VK_ESCAPE);
                byte ekDown = (byte)(((ek & 0x8000) != 0) ? 1 : 0);
                if (ekDown == 1 && prevEscK == 0)
                {
                    escKsHit++;
                    if (!escKeyState) { escKeyState = true; escKeyStateMs = Ms(); }
                    Console.WriteLine("[" + Ms() + "ms] [实验2 GetKeyState]      ESC 按下 raw=0x" + ((ushort)ek).ToString("X4"));
                }
                prevEscK = ekDown;

                short sk = N.GetKeyState(N.VK_SPACE);
                if ((sk & 0x8000) != 0 && !spaceKeyState)
                {
                    spaceKeyState = true; spaceKeyStateMs = Ms();
                    Console.WriteLine("[" + Ms() + "ms] [实验2 GetKeyState]      空格按下 raw=0x" + ((ushort)sk).ToString("X4"));
                }

                // 实验 3：抽空泵一下窗口消息（不阻塞）
                if (Ms() - lastMsgPump > 10)
                {
                    lastMsgPump = Ms();
                    N.MSG m;
                    while (PeekMessage(out m, _hwnd))
                    {
                        N.TranslateMessage(ref m);
                        N.DispatchMessageW(ref m);
                    }
                }

                Thread.Sleep(1);
            }

            if (hot) N.UnregisterHotKey(_hwnd, 1);
            if (_hwnd != IntPtr.Zero) N.DestroyWindow(_hwnd);

            Console.WriteLine();
            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine("实验1 GetAsyncKeyState  ESC: " + Flag(escAsync) + " (" + escAsyncHit + " 次)  空格: " + Flag(spaceAsync) + " (" + spaceAsyncHit + " 次)");
            Console.WriteLine("实验2 GetKeyState       ESC: " + Flag(escKeyState) + " (" + escKsHit + " 次)  空格: " + Flag(spaceKeyState));
            Console.WriteLine("实验3 RegisterHotKey    ESC: " + Flag(_hotkeyFired));
            Console.WriteLine();
            Console.WriteLine("时序：ESC 首次 —— GetAsyncKeyState " + escAsyncMs + "ms / GetKeyState " + escKeyStateMs + "ms");
            Console.WriteLine("      空格首次 —— GetAsyncKeyState " + spaceAsyncMs + "ms / GetKeyState " + spaceKeyStateMs + "ms");
            return 0;
        }

        private static string Flag(bool b) { return b ? "✅ 读到" : "❌ 未读到"; }

        [DllImport("user32.dll")]
        private static extern bool PeekMessageW(out N.MSG m, IntPtr h, uint min, uint max, uint remove);

        private static bool PeekMessage(out N.MSG m, IntPtr h)
        {
            return PeekMessageW(out m, h, 0, 0, 0x0001);
        }
    }
}
