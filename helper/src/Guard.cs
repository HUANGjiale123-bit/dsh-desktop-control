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
            string blanked = Path.Combine(dir, "fx.blanked");
            string stop = Path.Combine(dir, "fx.stop");

            Log(dir, "guard 启动 pid=" + Process.GetCurrentProcess().Id);

            int idleRounds = 0;
            while (true)
            {
                bool marker = File.Exists(blanked);
                if (marker)
                {
                    idleRounds = 0;
                    int pid = 0;
                    try { int.TryParse(File.ReadAllText(blanked).Trim(), out pid); } catch { }

                    if (pid > 0 && !IsAlive(pid))
                    {
                        bool ok = SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
                        try { File.Delete(blanked); } catch { }
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
                        return 0;
                    }
                }

                Thread.Sleep(400);
            }
        }
    }
}
