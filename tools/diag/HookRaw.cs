/*
 * dsc-hookraw —— 零过滤的钩子回调计数
 *
 * 目的：把"回调从未被调用"与"回调被调用了但我的过滤条件把它挡掉"这两种情况
 * 彻底分开。之前的版本虽然也记录按键，但记录条件里带了对 flags 的判断，
 * 一旦真实按键带着意外的 flags（例如被标为注入），就会出现"回调在跑、日志却空白"
 * 的假象，从而把结论误导向"钩子收不到输入"。
 *
 * 本版规则：
 *   · 回调一进来就无条件 ++count，不做任何判断
 *   · 每一次回调都写一行，写明 nCode / wParam / vkCode / flags，
 *     注入与否只作为标注，不作为过滤条件
 *   · 心跳报告累计回调次数，即使一次按键都没被识别，也能看出回调是否在跑
 *
 * 用法：dsc-hookraw [秒数] [日志路径]
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class HookRaw
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
    const uint WM_QUIT = 0x0012;
    const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_LOWER_IL_INJECTED = 0x02;

    static HookProc _proc;                 // 静态持有，防 GC
    static IntPtr _hook = IntPtr.Zero;
    static string _log;
    static int _callbacks;                 // ★ 无条件计数：回调进来就 +1
    static int _logged;
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
        if (vk == 0x09) return "Tab";
        return "vk" + vk;
    }

    static string FlagText(uint f)
    {
        StringBuilder sb = new StringBuilder();
        if ((f & LLKHF_EXTENDED) != 0) sb.Append("EXTENDED ");
        if ((f & LLKHF_LOWER_IL_INJECTED) != 0) sb.Append("LOWER_IL_INJECTED ");
        if ((f & LLKHF_INJECTED) != 0) sb.Append("INJECTED ");
        return sb.Length == 0 ? "(none)" : sb.ToString().Trim();
    }

    static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // ★ 第一件事就是无条件计数。不做任何判断，不改任何分支。
        _callbacks++;

        try
        {
            if (_logged < 40)
            {
                _logged++;
                string detail = "nCode=" + nCode + " wParam=0x" + wParam.ToInt32().ToString("X4");
                if (nCode == HC_ACTION && lParam != IntPtr.Zero)
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    detail += " vk=0x" + k.vkCode.ToString("X2") + "(" + KeyName(k.vkCode) + ")" +
                              " scan=0x" + k.scanCode.ToString("X2") +
                              " flags=0x" + k.flags.ToString("X2") + "[" + FlagText(k.flags) + "]";
                }
                L("回调 #" + _callbacks + ": " + detail);
            }
        }
        catch (Exception e)
        {
            L("回调内异常（不影响计数）: " + e.GetType().Name + ": " + e.Message);
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 40;
        _log = args.Length > 1 ? args[1] : @"D:\dsh workspace\dsh-desktop-control\.state\hookraw.log";
        try { if (File.Exists(_log)) File.Delete(_log); } catch { }

        Console.WriteLine("=== 零过滤钩子回调计数 (pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                          " tid=" + GetCurrentThreadId() + ") ===");

        _proc = Proc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(IntPtr.Zero), 0);
        if (_hook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            L("装钩子失败 Win32=" + err + ": " + new System.ComponentModel.Win32Exception(err).Message);
            return;
        }
        L("钩子已安装 handle=" + _hook);

        Thread hb = new Thread(delegate ()
        {
            int n = 0;
            while (!_stop && n < seconds)
            {
                Thread.Sleep(5000); n += 5;
                L("心跳 " + n + "s：回调累计 " + _callbacks + " 次" +
                  (_callbacks == 0 ? "  ← 回调一次都没被调用" : ""));
            }
        });
        hb.IsBackground = true; hb.Start();

        Thread stopper = new Thread(delegate ()
        {
            Thread.Sleep(seconds * 1000);
            _stop = true;
            PostThreadMessageW(GetCurrentThreadId(), WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        });
        stopper.IsBackground = true; stopper.Start();

        Console.WriteLine("请按任意键（ESC、字母都行）。" + seconds + " 秒后自动结束。");

        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        _stop = true;
        UnhookWindowsHookEx(_hook);
        L("=== 结束：回调总计 " + _callbacks + " 次 ===");
        if (_callbacks == 0)
            L("结论：钩子安装成功但回调从未被调用 —— Windows 没有把输入事件投递给本进程。");
        else
            L("结论：回调在跑，共 " + _callbacks + " 次 —— 钩子本身工作正常，之前的问题在过滤条件上。");
    }
}
