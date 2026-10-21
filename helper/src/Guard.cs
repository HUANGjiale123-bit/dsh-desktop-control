using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DscGuard
{
    /// <summary>
    /// 光标看门狗：装饰进程被强杀时，把系统光标恢复回来。
    ///
    /// ── 为什么必须有这个独立进程 ──
    ///
    /// SetSystemCursor 改的是**系统全局**资源，进程一死它不会自动还原。
    /// 装饰进程自己当然会在退出路径里恢复，但"被 taskkill / 崩了 / 断电前的异常"
    /// 都走不到那条路径 —— 实测过：kill 之后屏幕上就再也没有光标了，
    /// 只能手动执行 dsc.exe cursor_restore。
    ///
    /// 所以恢复这件事必须由**另一个还活着的进程**负责。它只做三件事：
    /// 看 fx.blanked 里的 PID、发现人死了就 SPI_SETCURSORS、写一行日志。
    /// 它不碰窗口、不碰输入，出错也只是"本该恢复却没恢复"，不会再添新问题。
    ///
    /// ── 为什么要读 PID 而不是只判断文件在不在 ──
    ///
    /// 装饰进程正常退出时会自己删掉 marker。文件还在 = 它没走到清理路径。
    /// 但进程可能正处于"刚写 marker、还没开始画"的瞬间，所以还要确认 PID 真的没了。
    /// </summary>
    internal static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfoW(int action, int param, IntPtr data, int winIni);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

        private const int SPI_SETCURSORS = 0x0057;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int STILL_ACTIVE = 259;

        private static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                int code;
                if (!GetExitCodeProcess(h, out code)) return false;
                return code == STILL_ACTIVE;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static void Log(string dir, string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "guard.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static int Main(string[] args)
        {
            if (args.Length < 2 || args[0] != "--watch")
            {
                Console.Error.WriteLine("用法: dsc-guard.exe --watch <状态目录>");
                return 2;
            }

            string dir = args[1];
            // 本轮实例标识：与 fx 用同一个 --run，ready 文件据此区分新旧实例
            string myRun = "";
            for (int ai = 2; ai < args.Length - 1; ai++)
            {
                if (args[ai] == "--run") { myRun = args[ai + 1]; break; }
            }
            string blanked = Path.Combine(dir, "fx.blanked");
            string stop = Path.Combine(dir, "fx.stop");
            string beat = Path.Combine(dir, "fx.heartbeat");

            // 就绪标记：装饰进程必须先看到它才允许隐藏系统光标。
            // 复核（2.18.71）：看门狗启动失败时 fx 仍在隐藏光标 —— 没有恢复者。
            string ready = Path.Combine(dir, "guard.ready");
            try
            {
                File.WriteAllText(ready,
                    "{\"runId\":\"" + myRun + "\",\"pid\":" + Process.GetCurrentProcess().Id + "}");
            }
            catch { }
            Log(dir, "guard 启动 pid=" + Process.GetCurrentProcess().Id);

            int idleRounds = 0;
            int noFxRounds = 0;
            while (true)
            {
                // 宿主（插件）心跳停了就是"插件已经没了"：先恢复光标，再退出。
                // 这条比"等 dsc-fx 消失再数 24 秒"快得多，卸载体验更好。
                bool hostGone = false;
                try
                {
                    if (File.Exists(beat))
                        hostGone = (DateTime.UtcNow - File.GetLastWriteTimeUtc(beat)).TotalSeconds > 25;
                }
                catch { }

                bool marker = File.Exists(blanked);
                if (hostGone)
                {
                    // 恢复失败时保留标记，并且**不能退出** —— 否则磁盘上只剩标记，
                    // 没有进程会去执行恢复（复核 2.18.72）。
                    bool restoredHere = true;
                    if (marker) restoredHere = SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
                    if (restoredHere)
                    {
                        try { if (marker) File.Delete(blanked); } catch { }
                        Log(dir, "guard 退出（宿主心跳已停止，插件已卸载或退出）");
                        try { File.Delete(ready); } catch { }
                        return 0;
                    }
                    Log(dir, "宿主消失但恢复失败：保留标记，2 秒后重试");
                    System.Threading.Thread.Sleep(2000);
                    continue;
                }

                if (marker)
                {
                    idleRounds = 0;
                    int pid = 0;
                    try { int.TryParse(File.ReadAllText(blanked).Trim(), out pid); } catch { }

                    if (pid > 0 && !IsAlive(pid))
                    {
                        bool ok = SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
                        // 成功才删标记；失败保留，下一轮继续尝试恢复
                        if (ok) { try { File.Delete(blanked); } catch { } }
                        Log(dir, "恢复光标（装饰进程 " + pid + " 已退出）ok=" + ok);
                    }
                }
                else
                {
                    idleRounds++;

                    // 没有 marker 且收到 stop → 收工。给 5 秒缓冲，避免和装饰进程
                    // 的创建顺序抢跑（插件可能先起 guard 再起 fx）。
                    if (File.Exists(stop) && idleRounds > 12)
                    {
                        Log(dir, "guard 退出（收到 fx.stop）");
                        try { File.Delete(ready); } catch { }
                    return 0;
                    }

                    // 另一个退出条件：装饰进程不在了、也没有要恢复的光标，
                    // 连续 30 秒都是这样 → 自己退出。插件被卸载时不会有人给我们写
                    // fx.stop，不靠这条就会永远挂着（实测：卸载后留下孤儿进程）。
                    bool fxRunning = false;
                    try
                    {
                        foreach (var p in Process.GetProcessesByName("dsc-fx"))
                        {
                            fxRunning = true;
                            break;
                        }
                    }
                    catch { fxRunning = true; }   // 查不到就当它还在，不冒险
                    if (fxRunning) noFxRounds = 0; else noFxRounds++;
                    // 400ms × 60 ≈ 24 秒。fx 自己在 25 秒内退出，所以最迟约 50 秒
                    // 两个进程都会消失；卸载时不再需要手动 taskkill。
                    if (noFxRounds > 60)
                    {
                        Log(dir, "guard 退出（装饰进程已不在，且无需恢复）");
                        try { File.Delete(ready); } catch { }
                    return 0;
                    }
                }

                Thread.Sleep(400);
            }
        }
    }
}
