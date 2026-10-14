/*
 * dsc-hooktest —— 最小可证的 WH_KEYBOARD_LL 钩子
 *
 * 目的：在"窗口站/桌面/会话都正确"这个已知前提下，用最小代码验证钩子
 * 到底能不能收到按键。如果这个最简版本也收不到，那就不是集成问题，
 * 而是这个进程环境对低层钩子的某种限制；如果能收到，问题就在 dsc-edge 的
 * 集成方式上（消息循环、STA、计时器交互等）。
 *
 * 关键设计：**不用计时器、不用窗口、不用 WinForms**。
 * 只做三件事：装钩子 → 阻塞式 GetMessageW 循环 → 回调里打印并写日志。
 * 心跳由另一个线程写，用来证明"日志通道活着"。
 *
 * 用法：dsc-hooktest [秒数]   默认 40
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class HookTest
{
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint tid);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("user32.dll")]
    static extern int GetMessageW(out MSG m, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")]
    static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    const int WH_KEYBOARD_LL = 13;
    const int HC_ACTION = 0;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const uint LLKHF_INJECTED = 0x10;
    const uint WM_QUIT = 0x0012;
    const int VK_ESCAPE = 0x1B;

    // 委托必须静态持有，否则被 GC 回收后钩子静默失效
    static HookProc _proc;
    static IntPtr _hook = IntPtr.Zero;
    static string _log;
    static int _count;
    static volatile bool _stop;

    static void L(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s;
        Console.WriteLine(line);
        try { File.AppendAllText(_log, line + Environment.NewLine); } catch { }
    }

    static string KeyName(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk == 0x1B) return "ESC";
        if (vk == 0x20) return "Space";
        if (vk == 0x0D) return "Enter";
        return "vk" + vk;
    }

    static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == HC_ACTION)
            {
                int m = wParam.ToInt32();
                if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool injected = (k.flags & LLKHF_INJECTED) != 0;
                    _count++;
                    L("收到按键 vk=0x" + k.vkCode.ToString("X2") + " (" + KeyName(k.vkCode) + ")" +
                      (injected ? " [注入]" : " [真实]") + " flags=0x" + k.flags.ToString("X2"));
                    if (k.vkCode == VK_ESCAPE && !injected)
                        L(">>> ESC 真实按下 <<<");
                }
            }
        }
        catch (Exception e) { L("回调异常: " + e.Message); }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 40;
        _log = args.Length > 1 ? args[1] : @"D:\dsh workspace\dsh-desktop-control\.state\hooktest.log";

        try { if (File.Exists(_log)) File.Delete(_log); } catch { }

        Console.WriteLine("=== 最小 WH_KEYBOARD_LL 钩子测试 (pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                          " tid=" + GetCurrentThreadId() + ") ===");

        _proc = Proc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(IntPtr.Zero), 0);
        if (_hook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            L("装钩子失败 Win32=" + err + ": " + new System.ComponentModel.Win32Exception(err).Message);
            return;
        }
        L("钩子已安装 handle=" + _hook + "  日志=" + _log);

        // 心跳线程：证明日志通道活着（否则"没收到按键"无法与"日志没写"区分）
        Thread hb = new Thread(delegate ()
        {
            int n = 0;
            while (!_stop && n < seconds)
            {
                Thread.Sleep(5000);
                n += 5;
                L("心跳 " + n + "s：累计按键 " + _count);
            }
        });
        hb.IsBackground = true;
        hb.Start();

        // 定时退出：用 PostThreadMessage 打断阻塞的 GetMessage
        Thread stopper = new Thread(delegate ()
        {
            Thread.Sleep(seconds * 1000);
            _stop = true;
            PostThreadMessageW(GetCurrentThreadId(), WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        });
        stopper.IsBackground = true;
        stopper.Start();

        Console.WriteLine("现在按 ESC（也可以按别的键做对照）。" + seconds + " 秒后自动结束。");

        // 阻塞式消息循环。低层钩子的回调就在这个循环里被 Windows 调用。
        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        _stop = true;
        UnhookWindowsHookEx(_hook);
        L("=== 结束：累计收到 " + _count + " 次按键 ===");
    }
}
