/*
 * 窗口激活 —— 照 Codex 的 activate_window 设计
 *
 * 为什么必须有这一层（实测结论，不是推理）：
 *
 *   本项目的输入注入会"时好时坏"。追查很久后发现，规律是：
 *   目标窗口已经是前台时注入成功，不是前台时失败。
 *   而 SetForegroundWindow 从后台进程调用会被系统拒绝，
 *   于是尝试激活失败后，我就会硬着头皮注入——进入"合成输入被压制"的状态。
 *
 *   Codex 的处理方式是：**输入类方法自动激活目标窗口**，
 *   并在失败时走"激活 → 重取状态 → 重试一次"的恢复流程，
 *   而不是在错误状态下继续发输入。
 *
 * 激活手法（按可靠性排序，逐个尝试）：
 *   1. 已在前台        → 直接返回
 *   2. 最小化则先还原
 *   3. AttachThreadInput 附加到当前前台线程，绕开"非前台进程不能抢焦点"
 *   4. BringWindowToTop + SetForegroundWindow
 *   5. 短暂置顶（TOPMOST → NOTOPMOST）确保真的到最前
 *   6. 全部失败 → 明确报错，交给调用方决定，不静默继续
 */

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dsc
{
    internal static class WindowActivator
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
        private const int SW_RESTORE = 9;

        /// <summary>激活结果。失败时 message 说明原因，调用方应据此拒绝注入。</summary>
        public sealed class Result
        {
            public bool Ok;
            public bool AlreadyForeground;
            public bool WasMinimized;
            public string Message;
        }

        /// <summary>
        /// 把目标窗口激活到前台。成功返回 Ok=true。
        ///
        /// **失败必须被当作硬错误处理**——在非前台状态下注入会进入
        /// 合成输入被系统压制的状态，那比直接失败更糟（表现为"注入无反应但无报错"）。
        /// </summary>
        public static Result Activate(IntPtr hwnd)
        {
            Result r = new Result();

            if (hwnd == IntPtr.Zero)
            {
                r.Message = "窗口句柄为空";
                return r;
            }
            if (!IsWindowVisible(hwnd))
            {
                r.Message = "目标窗口不可见（可能已关闭或隐藏）";
                return r;
            }

            // 最小化与"不可见"是两回事：实测最小化的窗口 IsWindowVisible 仍返回 True，
            // 于是"不可见就拒绝"这条分支放行了，而 UIA 一直给出 -32000 级别的哨兵矩形，
            // 最终表现为"元素矩形无效"。所以必须先单独判断并还原最小化。
            if (IsIconic(hwnd))
            {
                r.WasMinimized = true;
                ShowWindow(hwnd, SW_RESTORE);
                Thread.Sleep(300);
            }

            if (GetForegroundWindow() == hwnd)
            {
                r.Ok = true;
                r.AlreadyForeground = true;
                return r;
            }

            // 附加到当前前台线程的输入队列，绕开前台锁定
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : (uint)GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint myThread = GetCurrentThreadId();
            bool attached = false;

            try
            {
                if (fgThread != 0 && fgThread != myThread)
                    attached = AttachThreadInput(myThread, fgThread, true);

                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                Thread.Sleep(120);

                if (GetForegroundWindow() != hwnd)
                {
                    // 短暂置顶再取消：确保真的到最前，且不留下"永远置顶"的副作用
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    Thread.Sleep(80);
                    SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(180);
                }
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }

            IntPtr now = GetForegroundWindow();
            if (now == hwnd)
            {
                r.Ok = true;
                return r;
            }

            r.Message = "无法把窗口 " + hwnd + " 激活到前台（当前前台是 " + now + "）。" +
                        "在非前台状态下注入会被系统压制，表现为\"注入无反应但不报错\"，" +
                        "因此这里直接拒绝而不是继续。请先把目标窗口切到前台再重试。";
            return r;
        }
    }
}
