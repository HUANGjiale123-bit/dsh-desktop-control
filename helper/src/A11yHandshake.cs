/* Chromium 辅助技术（AT）握手。
 *
 * ── 它解决的是哪一种情况 ──
 *
 * 探测发现有两类目标拿不到元素：
 *
 *   ① 没有内容 HWND（Edge 155 用 DComp 渲染，只有 Intermediate D3D Window）
 *   ② 有内容 HWND 但树没建（内容窗口只返回 1 个节点）
 *
 * 这个握手针对 ②。Chromium 在检测到辅助技术访问时会建立完整的无障碍树，
 * 而"检测"的一条历史路径是：
 *
 *   Chromium 向系统发出 EVENT_SYSTEM_ALERT（自定义对象 ID = 1）
 *   → 辅助技术用 AccessibleObjectFromEvent 响应
 *   → Chromium 认为有 AT 在用，开始建树
 *
 * ── 为什么不能直接判成败 ──
 *
 * 外部复核的原话："Chromium 该分支可以处理请求但不返回对象"。
 * 所以 `AccessibleObjectFromWindow(hwnd, 1, ...)` 返回 E_FAIL
 * **不代表握手没触发**。这个模块把"发了什么、收到什么、之后树变没变"
 * 全部记下来，让判断建立在数据上而不是猜测上。
 *
 * ── 实现边界 ──
 *
 * · 只用 WINEVENT_OUTOFCONTEXT：**不向目标进程注入任何 DLL**。
 * · 钩子绑到目标 PID，不接收全系统事件。
 * · 用 PeekMessage 轮询泵消息，不建窗口 —— dsc.exe 是控制台程序，
 *   本来没有消息循环，但 OUTOFCONTEXT 的回调需要它。
 * · 全程有超时：provider 卡住时不能把调用方钉死在这里。
 * · 结束一定 UnhookWinEvent，不留悬挂钩子。
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dsc
{
    internal static class A11yHandshake
    {
        private const uint EVENT_SYSTEM_ALERT = 0x0002;
        private const uint EVENT_MIN = 0x00000001;
        private const uint EVENT_MAX = 0x7FFFFFFF;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int OBJID_CLIENT = -4;
        private const int CHILDID_SELF = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromEvent(
            IntPtr hwnd, int dwId, int dwChildId,
            [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible ppacc,
            [MarshalAs(UnmanagedType.Struct)] out object pvarChild);

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private const uint PM_REMOVE = 0x0001;

        /// <summary>握手期间收到的事件（回调里只记，不做重活）。</summary>
        private sealed class Collected
        {
            public int EventCount;
            public int AlertObjectId;
            public int AlertChildId;
            public IntPtr AlertHwnd;
            public int RespondedHr;
            public bool RespondedOk;
        }

        private static Collected _collected;
        // 必须保持引用：回调被 GC 回收会导致进程崩溃（经典的 delegate 生命周期坑）
        private static WinEventDelegate _callback;

        /// <summary>
        /// 对一个窗口做 AT 握手：挂事件钩子、泵一小段时间、必要时响应。
        ///
        /// 返回一份诊断字典 —— **不返回"成功/失败"**，因为这件事没有可靠的
        /// 成败判据（见文件头）。调用方应当对比握手前后的节点数。
        /// </summary>
        internal static Dictionary<string, object> Run(IntPtr hwnd, uint pid, int budgetMs)
        {
            var info = new Dictionary<string, object>();
            if (pid == 0)
            {
                info["skipped"] = "pid 未知";
                return info;
            }
            if (budgetMs <= 0 || budgetMs > 5000) budgetMs = 800;

            // 每次运行都要重置：上一轮的 "已响应" 状态泄漏到这一轮，
            // 会让第二次探测永远不响应事件。
            _responded = false;
            _collected = new Collected();
            IntPtr hook = IntPtr.Zero;
            try
            {
                _callback = OnWinEvent;
                hook = SetWinEventHook(
                    EVENT_MIN, EVENT_MAX, IntPtr.Zero, _callback,
                    pid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

                if (hook == IntPtr.Zero)
                {
                    info["hook"] = "failed";
                    info["note"] = "SetWinEventHook 返回 0（可能目标进程没有事件或权限不足）";
                    return info;
                }
                info["hook"] = "installed";

                // 泵消息。OUTOFCONTEXT 的回调靠线程消息队列分发，
                // 不泵的话钩子装了也收不到东西。
                long deadline = Environment.TickCount + budgetMs;
                while (Environment.TickCount < deadline)
                {
                    MSG msg;
                    while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                    Thread.Sleep(10);
                }

                info["events"] = _collected.EventCount;
                info["alertObjectId"] = _collected.AlertObjectId;
                info["alertChildId"] = _collected.AlertChildId;
                info["alertHwnd"] = _collected.AlertHwnd.ToInt64();
                info["responded"] = _collected.RespondedOk;
                info["respondHr"] = "0x" + (_collected.RespondedHr & 0xFFFFFFFF).ToString("X8");

                if (_collected.EventCount == 0)
                {
                    info["note"] =
                        "窗口内没收到任何事件。目标可能已经建好树（不需要握手），" +
                        "也可能根本不用这条历史路径。两种都可能，不能据此判定。";
                }
                return info;
            }
            catch (Exception e)
            {
                info["error"] = e.GetType().Name + ": " + e.Message;
                return info;
            }
            finally
            {
                if (hook != IntPtr.Zero)
                {
                    try { UnhookWinEvent(hook); } catch { }
                }
                _callback = null;
            }
        }

        /// <summary>
        /// 事件回调。
        ///
        /// **这里只做收集和一次响应**：外部复核明确说 callback 内不要同步遍历整棵树，
        /// 重入时会卡住事件线程。
        /// </summary>
        private static void OnWinEvent(
            IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time)
        {
            Collected c = _collected;
            if (c == null) return;
            c.EventCount++;

            // Chromium 的历史握手用的是对象 ID = 1 的 EVENT_SYSTEM_ALERT
            if (eventType == EVENT_SYSTEM_ALERT && idObject == 1)
            {
                c.AlertObjectId = idObject;
                c.AlertChildId = idChild;
                if (c.AlertHwnd == IntPtr.Zero) c.AlertHwnd = hwnd;

                // 用 AccessibleObjectFromEvent 响应一次，告诉它"有 AT 在用"。
                // 只做一次：反复响应没有意义，而且可能让 provider 忙。
                if (!c.RespondedOk && !_responded)
                {
                    _responded = true;
                    try
                    {
                        Accessibility.IAccessible acc;
                        object child;
                        int hr = AccessibleObjectFromEvent(
                            hwnd, idObject, idChild, out acc, out child);
                        c.RespondedHr = hr;
                        c.RespondedOk = hr >= 0 && acc != null;
                    }
                    catch (Exception e)
                    {
                        c.RespondedHr = unchecked((int)0x80004005);
                        c.RespondedOk = false;
                        void_ignore(e);
                    }
                }
            }
        }

        private static bool _responded;

        /** 让编译器别抱怨未使用的 catch 变量；同时保持"异常不吞掉语义"可读。 */
        private static void void_ignore(Exception e) { }

        // OBJID_CLIENT / CHILDID_SELF 在这里只是文档性常量，
        // 真正用它们的是 Msaa.cs。留着是为了让读这段代码的人知道
        // 握手和正常的 OBJID_CLIENT 查询是两回事。
        private static readonly int _unused = OBJID_CLIENT + CHILDID_SELF;
    }
}
