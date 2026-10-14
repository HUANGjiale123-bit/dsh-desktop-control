/*
 * 读路径冒烟测试（第 0 步）
 *
 * 目的：在动 set_value 之前，确认最小读操作在当前进程/线程/apartment 下可用。
 *
 * 为什么必须先做：如果读路径本身会崩，那么后面测 set_value 失败就无法区分
 * 「控件不支持」和「我的 UIA 客户端上下文有问题」——会把环境问题误读成功能
 * 限制，进而引向错误的补救（多进程、原生 COM 互操作）。
 *
 * 本程序刻意只做最小的事，且全部调用留在**同一个线程**上：
 *   1. 枚举窗口，挑一个有标题的
 *   2. 从它的 HWND 取根 AutomationElement
 *   3. 用 FindAll(Subtree) 抓子树（一次往返，不走 TreeWalker）
 *   4. 逐条读几个属性（Current.Name / ControlType / IsEnabled）
 *   5. 顺便探测哪些元素实现了 ValuePattern / RangeValuePattern
 *
 * 每一项都单独 try/catch 并把异常类型原样打印——异常类型本身就是结论。
 *
 * 编译：见 build.sh 里的 smoke 目标，或直接：
 *   csc -nologo -target:exe -out:bin/dsc-smoke.exe -r:... src/Smoke.cs
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace DscSmoke
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr p);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pv, uint flags);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetCurrentThreadId();

        public const uint COINIT_MULTITHREADED = 0x0;
        public const uint COINIT_APARTMENTTHREADED = 0x2;
    }

    internal static class Program
    {
        private static string Title(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(512);
            Native.GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ExType(Exception e)
        {
            if (e == null) return "";
            COMException ce = e as COMException;      // C# 5 写法（旧 csc 不支持模式匹配）
            if (ce != null) return "COMException(0x" + ce.ErrorCode.ToString("X8") + ")";
            return e.GetType().Name;
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            int tid = Native.GetCurrentThreadId();
            Console.WriteLine("=== 读路径冒烟测试 ===");
            Console.WriteLine("线程 id        : " + tid);

            // 显式声明线程 apartment。控制台程序默认 MTA，这里显式调用以便观察返回值：
            //   0x0 = S_OK（首次初始化）
            //   0x1 = S_FALSE（已初始化过，同模式）
            // 0x80010106 = RPC_E_CHANGED_MODE（该线程已是别的 apartment —— 需要警惕）
            int hr = Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_MULTITHREADED);
            Console.WriteLine("CoInitializeEx(MTA) : 0x" + hr.ToString("X8") +
                (hr == 0 ? "  (S_OK, 本线程为 MTA)" :
                 hr == 1 ? "  (S_FALSE, 已是 MTA)" :
                 hr == unchecked((int)0x80010106) ? "  (RPC_E_CHANGED_MODE —— 本线程已被初始化成别的 apartment！)" :
                 "  (其它)"));

            // 1) 找一个有标题的可见窗口
            List<IntPtr> wins = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                if (Native.IsWindowVisible(h) && Title(h).Length > 0) wins.Add(h);
                return true;
            }, IntPtr.Zero);
            Console.WriteLine("有标题的可见窗口 : " + wins.Count);
            if (wins.Count == 0) { Console.WriteLine("没有可用窗口，退出"); return 2; }

            IntPtr target = wins[0];
            uint pid;
            Native.GetWindowThreadProcessId(target, out pid);
            Console.WriteLine("测试目标        : hwnd=" + target + "  pid=" + pid + "  title=\"" + Title(target) + "\"");

            // 2) 取根元素
            AutomationElement root = null;
            try
            {
                root = AutomationElement.FromHandle(target);
                Console.WriteLine("[1] FromHandle        : OK  name=\"" + SafeName(root) + "\"");
            }
            catch (Exception e)
            {
                Console.WriteLine("[1] FromHandle        : 失败 -> " + ExType(e) + ": " + e.Message);
                return 3;
            }

            // 3) 一次性抓子树（不走 TreeWalker，避免 N 次跨进程往返）
            AutomationElementCollection all = null;
            DateTime t0 = DateTime.Now;
            try
            {
                all = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
                Console.WriteLine("[2] FindAll(Subtree)  : OK  取回 " + all.Count + " 个元素，耗时 " +
                    (DateTime.Now - t0).TotalMilliseconds.ToString("F0") + " ms");
            }
            catch (Exception e)
            {
                Console.WriteLine("[2] FindAll(Subtree)  : 失败 -> " + ExType(e) + ": " + e.Message);
                return 4;
            }

            // 4) 逐条读属性 —— 这一步最接近真正会崩的地方
            int nameOk = 0, nameFail = 0, ctypeOk = 0, ctypeFail = 0;
            int withValue = 0, withRange = 0, readOnly = 0;
            string firstException = null;
            int limit = Math.Min(all.Count, 400);

            for (int i = 0; i < limit; i++)
            {
                AutomationElement el = all[i];
                try { string n = el.Current.Name; nameOk++; }
                catch (Exception e) { nameFail++; if (firstException == null) firstException = "Current.Name -> " + ExType(e) + ": " + e.Message; }

                try { ControlType ct = el.Current.ControlType; ctypeOk++; }
                catch (Exception e) { ctypeFail++; if (firstException == null) firstException = "Current.ControlType -> " + ExType(e) + ": " + e.Message; }

                object vp = null, rvp = null;
                try { if (el.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) withValue++; } catch { }
                try { if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out rvp)) withRange++; } catch { }
                if (vp != null)
                {
                    try { if (((ValuePattern)vp).Current.IsReadOnly) readOnly++; } catch { }
                }
            }

            Console.WriteLine("[3] 读属性（前 " + limit + " 个）:");
            Console.WriteLine("      Current.Name        : 成功 " + nameOk + "  失败 " + nameFail);
            Console.WriteLine("      Current.ControlType : 成功 " + ctypeOk + "  失败 " + ctypeFail);
            Console.WriteLine("      实现 ValuePattern   : " + withValue + "（其中只读 " + readOnly + "）");
            Console.WriteLine("      实现 RangeValuePattern : " + withRange);
            if (firstException != null) Console.WriteLine("      首个异常            : " + firstException);

            // 5) 二次访问同一批元素——验证句柄是否还能用（过期语义）
            int secondOk = 0, secondFail = 0;
            string secondErr = null;
            for (int i = 0; i < limit; i++)
            {
                try { string n = all[i].Current.Name; secondOk++; }
                catch (Exception e) { secondFail++; if (secondErr == null) secondErr = ExType(e) + ": " + e.Message; }
            }
            Console.WriteLine("[4] 二次访问同批元素 : 成功 " + secondOk + "  失败 " + secondFail +
                (secondErr != null ? "  首个异常: " + secondErr : ""));

            Native.CoUninitialize();

            Console.WriteLine();
            Console.WriteLine(nameFail == 0 && ctypeFail == 0
                ? "结论: ✅ 读路径可用（本线程 MTA，单线程内完成，无异常）"
                : "结论: ❌ 读路径存在异常，先修它再谈写操作");
            return 0;
        }

        private static string SafeName(AutomationElement el)
        {
            try { return el.Current.Name ?? ""; } catch (Exception e) { return "<" + ExType(e) + ">"; }
        }
    }
}
