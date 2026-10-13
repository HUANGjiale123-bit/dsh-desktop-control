/*
 * dsh-desktop-control — Windows 辅助进程
 *
 * 设计要点（见 docs/设计.md）：
 *   1. 启动即声明 DPI 感知，使 Win32 / UIA / 抓屏三方坐标统一到物理像素。
 *      这是关键：不做任何缩放换算，而是让三个来源本来就一致。
 *   2. 无障碍元素以 RuntimeId 作键存入本进程的引用表，不做 PropertyCondition
 *      批量查找（旧编译器 + 类型细节容易踩坑）。
 *   3. 输出为紧凑 JSON，字段名固定。
 *
 * 编译（Git Bash）：
 *   MSYS2_ARG_CONV_EXCL='*' csc -nologo -target:exe -out:bin/dsc.exe \
 *     -r:System.Windows.Forms.dll \
 *     -r:/c/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF/UIAutomationClient.dll \
 *     -r:/c/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF/UIAutomationTypes.dll \
 *     src/*.cs
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Dsc
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageNameW(
            IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out POINT p);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }

    /// <summary>极简 JSON 写出器：避免引入外部依赖。</summary>
    internal sealed class Jw
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private bool _needComma;

        public Jw Obj() { Sep(); _sb.Append('{'); _needComma = false; return this; }
        public Jw End() { _sb.Append('}'); _needComma = true; return this; }
        public Jw Arr() { Sep(); _sb.Append('['); _needComma = false; return this; }
        public Jw EndArr() { _sb.Append(']'); _needComma = true; return this; }

        private void Sep() { if (_needComma) _sb.Append(','); _needComma = true; }

        public Jw Key(string k) { Sep(); _sb.Append(Quote(k)).Append(':'); _needComma = false; return this; }
        public Jw Val(string s) { Sep(); _sb.Append(s == null ? "null" : Quote(s)); return this; }
        public Jw Num(long n) { Sep(); _sb.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture)); return this; }
        public Jw Bool(bool b) { Sep(); _sb.Append(b ? "true" : "false"); return this; }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            StringBuilder b = new StringBuilder(s.Length + 8);
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
            return b.ToString();
        }

        public override string ToString() { return _sb.ToString(); }
    }

    internal static class Program
    {
        /// <summary>
        /// 急停闸门文件的位置。
        ///
        /// 存在即表示"停"：此后**所有变更性动作一律拒绝**，观察类操作不受影响
        /// ——急停的目的是停手，不是把眼睛也蒙上。
        ///
        /// 位置优先级：
        ///   1. 环境变量 DSC_HALT_GATE（便于测试与特殊部署）
        ///   2. 辅助进程所在目录的上一级（即仓库根）下的 .state\halt
        ///
        /// **刻意不放在 %LOCALAPPDATA%**：实测某些受限环境下对该目录的写入
        /// 会被拒绝，而一个写不进去的急停闸门等于没有急停。仓库目录是随辅助
        /// 进程一起部署的，必然可写。
        /// </summary>
        private static string GatePath()
        {
            string fromEnv = Environment.GetEnvironmentVariable("DSC_HALT_GATE");
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

            // AppContext.BaseDirectory = 可执行文件所在目录
            string binDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string root = Path.GetDirectoryName(binDir);      // helper\bin -> helper
            if (!string.IsNullOrEmpty(root)) root = Path.GetDirectoryName(root);  // helper -> 仓库根
            if (string.IsNullOrEmpty(root)) root = binDir;
            return Path.Combine(root, ".state", "halt");
        }

        private static bool IsHalted()
        {
            try { return File.Exists(GatePath()); }
            catch { return false; }
        }

        /// <summary>
        /// 拉起急停闸门。
        ///
        /// **失败必须响亮地失败。** 早先这里把异常整个吞掉，结果是：闸门文件
        /// 根本没写成，命令却回报 halted=true。使用者以为已经停了，实际没有——
        /// 对逃生舱而言这是最坏的一类 bug，比没有逃生舱更危险。
        /// 现在写不成就抛错，命令以非零码结束，调用方一定看得见。
        /// </summary>
        private static void EnsureHalt(string reason)
        {
            string p = GatePath();
            string dir = Path.GetDirectoryName(p);
            Directory.CreateDirectory(dir);          // 失败就让异常冒出去
            File.WriteAllText(p, reason + Environment.NewLine +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);

            if (!File.Exists(p))
                throw new InvalidOperationException("急停闸门写入后仍不存在：" + p);
        }

        /// <summary>变更性动作的统一入口：先过急停闸门。</summary>
        private static void RequireNotHalted(string action)
        {
            if (!IsHalted()) return;
            throw new InvalidOperationException(
                "已被急停：闸门文件存在（" + GatePath() + "），拒绝执行 " + action + "。" +
                "确认安全后用 `dsc.exe stop --clear` 解除。");
        }

        /// <summary>本进程内的元素引用表：短 id → 无障碍元素。</summary>
        private static readonly Dictionary<string, AutomationElement> Elements =
            new Dictionary<string, AutomationElement>(StringComparer.Ordinal);

        private static int _nextElementId = 1;

        private static int Main(string[] args)
        {
            Native.SetProcessDPIAware();   // ★ 必须先于任何窗口/坐标 API
            Console.OutputEncoding = Encoding.UTF8;

            string cmd = args.Length > 0 ? args[0] : "help";
            try
            {
                switch (cmd)
                {
                    case "list_windows": return CmdListWindows();
                    case "tree": return CmdTree(args);
                    case "screenshot": return CmdScreenshot(args);
                    case "click": return CmdClick(args);
                    case "click_element": return CmdClickElement(args);
                    case "set_value": return CmdSetValue(args);
                    case "do_action": return CmdDoAction(args);
                    case "type": return CmdType(args);
                    case "press_key": return CmdPressKey(args);
                    case "halt": return CmdHalt(args);
                    case "stop": return CmdStop();
                    case "ping": return CmdPing();
                    default: return CmdHelp();
                }
            }
            catch (Exception ex)
            {
                Jw e = new Jw();
                e.Obj().Key("ok").Bool(false)
                    .Key("error").Val(ex.GetType().Name)
                    .Key("message").Val(ex.Message)
                 .End();
                Console.WriteLine(e.ToString());
                return 1;
            }
        }

        private static int CmdHelp()
        {
            Console.WriteLine(@"dsh-desktop-control helper

  dsc.exe ping
  dsc.exe list_windows
  dsc.exe tree <hwnd> [maxNodes]
  dsc.exe screenshot <hwnd> [outPath]
");
            return 0;
        }

        private static int CmdPing()
        {
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("dpiAware").Bool(true)
                .Key("halted").Bool(IsHalted())
                .Key("gatePath").Val(GatePath())
                .Key("clr").Val(Environment.Version.ToString())
                .Key("os").Val(Environment.OSVersion.VersionString)
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 拉起急停闸门。
        ///
        /// 用法：dsc.exe halt [原因]
        /// 之后所有变更性动作（点击/输入/按键）一律失败，直到 stop --clear。
        /// </summary>
        private static int CmdHalt(string[] args)
        {
            string reason = args.Length > 1 ? args[1] : "由调用方触发";
            EnsureHalt(reason);
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("halted").Bool(true)
                .Key("gatePath").Val(GatePath())
                .Key("reason").Val(reason)
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>解除急停闸门。用法：dsc.exe stop --clear</summary>
        private static int CmdStop()
        {
            bool cleared = false;
            try
            {
                if (File.Exists(GatePath())) { File.Delete(GatePath()); cleared = true; }
            }
            catch { }
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("halted").Bool(IsHalted())
                .Key("cleared").Bool(cleared)
                .Key("gatePath").Val(GatePath())
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 截取窗口画面。
        ///
        /// 坐标是窗口在桌面上的物理像素矩形（因进程已声明 DPI 感知，
        /// 与 UIA 报告的 BoundingRectangle、Win32 GetWindowRect 三者同源）。
        /// 输出同时给出 origin，使调用方能把截图内坐标换算回桌面坐标——
        /// 这个换算是纯平移，不涉及任何缩放。
        /// </summary>
        private static int CmdScreenshot(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("screenshot 需要 hwnd 参数");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));

            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r))
                throw new InvalidOperationException("GetWindowRect 失败");
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) throw new InvalidOperationException("窗口尺寸无效: " + w + "x" + h);

            string outPath = args.Length > 2
                ? args[2]
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                      "dsc-" + hwnd.ToInt64().ToString("x") + "-" + DateTime.Now.Ticks.ToString() + ".png");
            string dir = System.IO.Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                bmp.Save(outPath, ImageFormat.Png);
            }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("filepath").Val(outPath)
                .Key("width").Num(w).Key("height").Num(h)
                .Key("originX").Num(r.Left).Key("originY").Num(r.Top)
                .Key("coords").Val("physical-pixels")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 在指定的桌面绝对物理像素坐标点击。
        ///
        /// 坐标到底以什么为准，由调用方通过基于哪一张截图来保证——
        /// 截图返回的 origin 加上图内偏移即为桌面坐标，全程只有平移，
        /// 没有任何缩放换算。这是与 dsh-click 最大的设计差异：
        /// 它把「读」与「验」分成两次独立抓取，坐标空间不一致导致互相矛盾。
        /// </summary>
        private static int CmdClick(string[] args)
        {
            // 急停检查必须是第一件事——放在坐标计算之后就等于没有闸门。
            RequireNotHalted("click");
            if (args.Length < 3) throw new ArgumentException("click 需要 x y 参数（桌面绝对物理像素）");
            int x = int.Parse(args[1]);
            int y = int.Parse(args[2]);
            bool right = args.Length > 3 && args[3] == "right";

            MoveCursorAbsolute(x, y);
            SendMouseButton(right, 1);

            // 回读光标位置：调用成功不代表点在了期望处，读回来才是证据。
            Native.POINT after;
            string actual = Native.GetCursorPos(out after) ? (after.X + "," + after.Y) : "?";

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("clickedX").Num(x).Key("clickedY").Num(y)
                .Key("cursorAfter").Val(actual)
                .Key("button").Val(right ? "right" : "left")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 取进程可执行文件路径。
        ///
        /// 首选 QueryFullProcessImageNameW：它只需要
        /// PROCESS_QUERY_LIMITED_INFORMATION，是权限要求最低的官方途径，
        /// 读取同用户会话内的进程通常都能成功。
        ///
        /// 备选 Process.MainModule——它需要 PROCESS_VM_READ，受限环境下常被拒。
        /// </summary>
        private static string ProcessPath(uint pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h != IntPtr.Zero)
                {
                    uint cap = 1024;
                    StringBuilder sb = new StringBuilder((int)cap);
                    if (Native.QueryFullProcessImageNameW(h, 0, sb, ref cap))
                        return sb.ToString();
                }
            }
            catch { }
            finally
            {
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            }

            try
            {
                return System.Diagnostics.Process.GetProcessById((int)pid).MainModule.FileName;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 按稳定描述符定位元素并点击其矩形中心。
        ///
        /// 描述符的字段：role=TYPE / name=TEXT / aid=AUTOMATIONID，
        /// 用 `/` 分隔，例如 `role=Button/name=保存`。
        ///
        /// 为什么不靠短 id：本程序是一次性进程，每次调用都是新进程，
        /// 上次的引用表已经没了。因此改为每次按稳定属性重新定位——
        /// 这样跨调用可用，也不必持有元素引用（那正是 PropertyCondition
        /// 类型细节容易出错的地方）。
        ///
        /// 点击用坐标而非 Invoke 模式：坐标与截图同处一个坐标空间，
        /// 所见即所点，不需要为每个控件类型判断该用哪个 Pattern。
        /// </summary>
        private static int CmdClickElement(string[] args)
        {
            RequireNotHalted("click_element");
            if (args.Length < 3) throw new ArgumentException("click_element 需要 hwnd 与描述符");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            string desc = args[2];
            bool right = args.Length > 3 && args[3] == "right";

            Dictionary<string, string> want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in desc.Split('/'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) want[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            if (want.Count == 0) throw new ArgumentException("描述符为空，例如 role=Button/name=保存");

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            AutomationElement found = FindByDescriptor(root, want, 0, 4000);
            if (found == null)
                throw new InvalidOperationException("未找到匹配元素：" + desc);

            Native.RECT r;
            GetElementRect(found, out r);
            if (r.Right <= r.Left || r.Bottom <= r.Top)
                throw new InvalidOperationException("匹配到的元素没有有效矩形，无法点击：" + desc);

            int cx = (r.Left + r.Right) / 2;
            int cy = (r.Top + r.Bottom) / 2;

            MoveCursorAbsolute(cx, cy);
            SendMouseButton(right, 1);

            Native.POINT after;
            string actual = Native.GetCursorPos(out after) ? (after.X + "," + after.Y) : "?";

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("matched").Val(desc)
                .Key("rect").Val(r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top))
                .Key("clickedX").Num(cx).Key("clickedY").Num(cy)
                .Key("cursorAfter").Val(actual)
                .Key("button").Val(right ? "right" : "left")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>按描述符深度优先查找第一个匹配元素。</summary>
        private static AutomationElement FindByDescriptor(
            AutomationElement el, Dictionary<string, string> want, int depth, int budget)
        {
            if (depth > 64 || budget <= 0) return null;

            bool match = true;
            foreach (KeyValuePair<string, string> kv in want)
            {
                string actual;
                switch (kv.Key.ToLowerInvariant())
                {
                    case "role":
                        actual = Safe(delegate { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });
                        break;
                    case "name":
                        actual = Safe(delegate { return el.Current.Name; });
                        break;
                    case "aid":
                        actual = Safe(delegate { return el.Current.AutomationId; });
                        break;
                    case "class":
                        actual = Safe(delegate { return el.Current.ClassName; });
                        break;
                    default:
                        actual = null;
                        break;
                }
                if (actual == null || string.Compare(actual, kv.Value, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    match = false;
                    break;
                }
            }
            if (match) return el;

            AutomationElement child = null;
            try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); } catch { }
            while (child != null)
            {
                AutomationElement hit = FindByDescriptor(child, want, depth + 1, budget - 1);
                if (hit != null) return hit;
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
            }
            return null;
        }

        /// <summary>
        /// 输入一段文本。
        ///
        /// 走 KEYEVENTF_UNICODE：直接投递 UTF-16 码元，因此中文、emoji 都能进，
        /// 也不受键盘布局影响。比"切成虚拟键码再模拟按键"可靠得多——
        /// 中文没有对应的虚拟键码，那条路根本走不通。
        ///
        /// 字符成对发送（按下 + 抬起）。
        /// </summary>
        /// <summary>
        /// set_value —— 通过 UIA 模式替换元素的值。**不经过输入注入。**
        ///
        /// 路径与 Codex 一致：先 ValuePattern，再 RangeValuePattern；
        /// 先读 read-only 状态，写之前先聚焦元素。**没有 LegacyIAccessible 回退**——
        /// 刻意不做，因为偷偷换一条路写进去会让"这个控件能不能写"的结论不可信。
        ///
        /// 三种失败原因分开报，不糊成一句"失败了"：
        ///   · 控件没实现模式      → NotSettable（控件级限制）
        ///   · 元素已从快照里消失  → StaleSnapshot（重新取快照）
        ///   · UIA 调用本身报错    → UiaError（客户端上下文问题）
        /// </summary>
        private static int CmdSetValue(string[] args)
        {
            RequireNotHalted("set_value");
            if (args.Length < 4) throw new ArgumentException("set_value 需要 hwnd index value");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            int index = int.Parse(args[2]);
            string value = args[3];

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            // 在同一次调用内重新抓一份快照再写：一次性进程模型下跨调用持不住快照，
            // 所以"索引仍然有效"只能靠这次重新绑定保证。
            Snapshot snap = SnapshotOps.Capture(root, hwnd, index + 1);
            AutomationElement el;
            if (!snap.Map.TryGetValue(index, out el))
                return FailJson("StaleSnapshot",
                    "element " + index + " is not present in window " + hwnd +
                    " (only " + snap.Listed + " elements listed); re-read the tree");

            if (SnapshotOps.SafeBool(delegate { return el.Current.IsPassword; }))
                return FailJson("Refused",
                    "element " + index + " is a password field; refusing to set its value");

            string role = SnapshotOps.SafeStr(delegate { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });

            object p;

            // ① ValuePattern
            bool hasValue = false;
            try { hasValue = el.TryGetCurrentPattern(ValuePattern.Pattern, out p); }
            catch (Exception ex) { return FailJson("UiaError", "TryGetCurrentPattern(ValuePattern) -> " + Describe(ex)); }

            if (hasValue)
            {
                try
                {
                    ValuePattern vp = (ValuePattern)p;
                    if (vp.Current.IsReadOnly)
                        return FailJson("ReadOnly",
                            "element " + index + " (" + role + ") is read-only; cannot set its value");

                    try { el.SetFocus(); } catch { }     // Codex 也是先聚焦
                    System.Threading.Thread.Sleep(40);

                    vp.SetValue(value);
                    return OkJson(hwnd, index, role, "ValuePattern", value);
                }
                catch (Exception ex)
                {
                    return FailJson("UiaError",
                        "ValuePattern.SetValue failed on element " + index + " (" + role + "): " + Describe(ex));
                }
            }

            // ② RangeValuePattern
            bool hasRange = false;
            try { hasRange = el.TryGetCurrentPattern(RangeValuePattern.Pattern, out p); }
            catch (Exception ex) { return FailJson("UiaError", "TryGetCurrentPattern(RangeValuePattern) -> " + Describe(ex)); }

            if (hasRange)
            {
                try
                {
                    RangeValuePattern rv = (RangeValuePattern)p;
                    if (rv.Current.IsReadOnly)
                        return FailJson("ReadOnly",
                            "element " + index + " (" + role + ") is read-only (range); cannot set its value");

                    double d;
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out d))
                        return FailJson("BadValue",
                            "element " + index + " exposes RangeValuePattern; its value must be a number, got: " + value);

                    double min = rv.Current.Minimum, max = rv.Current.Maximum;
                    if (d < min || d > max)
                        return FailJson("OutOfRange",
                            "value " + d + " is outside the allowed range [" + min + ", " + max + "]");

                    try { el.SetFocus(); } catch { }
                    System.Threading.Thread.Sleep(40);

                    rv.SetValue(d);
                    return OkJson(hwnd, index, role, "RangeValuePattern", value);
                }
                catch (Exception ex)
                {
                    return FailJson("UiaError",
                        "RangeValuePattern.SetValue failed on element " + index + " (" + role + "): " + Describe(ex));
                }
            }

            // ③ 两条路都没有 —— 控件级限制，收敛到 Codex 的原话
            return FailJson("NotSettable",
                "Cannot set a value for an element that is not settable: element " + index +
                " (" + role + ") implements neither ValuePattern nor RangeValuePattern. " +
                "This is a control limitation, not a client problem — no LegacyIAccessible fallback is attempted.");
        }

        /// <summary>
        /// do_action —— 触发元素的次要无障碍动作。**不经过输入注入。**
        ///
        /// 只接受七个标签（大小写不敏感），由 patterns 合成，与 Codex 一致：
        ///   Raise / Scroll Up / Scroll Down / Scroll Left / Scroll Right / Expand / Collapse
        ///
        /// 刻意不走 MSAA 的 accDoDefaultAction，也不接受猜出来的动作名——
        /// 动作名只能取自 tree 输出里的 Secondary Actions 列表。
        /// </summary>
        private static int CmdDoAction(string[] args)
        {
            RequireNotHalted("do_action");
            if (args.Length < 3) throw new ArgumentException("do_action 需要 hwnd index action");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            int index = int.Parse(args[2]);
            string wanted = NormalizeAction(args[3]);

            if (wanted == null)
                return FailJson("BadAction",
                    "unsupported secondary action: " + args[3] +
                    "; expected Raise, Scroll Up, Scroll Down, Scroll Left, Scroll Right, Expand, or Collapse");

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            Snapshot snap = SnapshotOps.Capture(root, hwnd, index + 1);
            AutomationElement el;
            if (!snap.Map.TryGetValue(index, out el))
                return FailJson("StaleSnapshot",
                    "element " + index + " is not present in window " + hwnd + "; re-read the tree");

            List<string> available;
            if (!snap.Actions.TryGetValue(index, out available) || available.Count == 0)
                return FailJson("NoActions",
                    "element " + index + " has no cached secondary actions for window " + hwnd);

            if (!available.Contains(wanted))
                return FailJson("ActionNotAvailable",
                    "secondary action '" + wanted + "' is not available for element " + index +
                    "; available actions: " + string.Join(", ", available.ToArray()));

            string role = SnapshotOps.SafeStr(delegate { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });

            try
            {
                object p;

                if (wanted == "Raise")
                {
                    if (!el.TryGetCurrentPattern(WindowPattern.Pattern, out p))
                        return FailJson("PatternLost", "element " + index + " no longer exposes WindowPattern");
                    // 置顶：先还原再前置，避免对已最小化的窗口直接 SetWindowVisualState 失败
                    ((WindowPattern)p).SetWindowVisualState(WindowVisualState.Normal);
                }
                else if (wanted.StartsWith("Scroll "))
                {
                    if (!el.TryGetCurrentPattern(ScrollPattern.Pattern, out p))
                        return FailJson("PatternLost", "element " + index + " no longer exposes ScrollPattern");
                    ScrollPattern sp = (ScrollPattern)p;
                    if (wanted == "Scroll Up") sp.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement);
                    else if (wanted == "Scroll Down") sp.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    else if (wanted == "Scroll Left") sp.Scroll(ScrollAmount.LargeDecrement, ScrollAmount.NoAmount);
                    else sp.Scroll(ScrollAmount.LargeIncrement, ScrollAmount.NoAmount);
                }
                else
                {
                    if (!el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p))
                        return FailJson("PatternLost", "element " + index + " no longer exposes ExpandCollapsePattern");
                    ExpandCollapsePattern ec = (ExpandCollapsePattern)p;
                    // ExpandCollapsePattern 的真实 API 是 Expand() / Collapse()，
                    // 没有可写属性（反射确认过，别再凭记忆写）。
                    if (wanted == "Expand") ec.Expand();
                    else ec.Collapse();
                }

                Jw j = new Jw();
                j.Obj().Key("ok").Bool(true)
                    .Key("hwnd").Num(hwnd.ToInt64())
                    .Key("index").Num(index)
                    .Key("role").Val(role)
                    .Key("action").Val(wanted)
                    .Key("revision").Num(snap.Revision)
                 .End();
                Console.WriteLine(j.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                return FailJson("UiaError",
                    "secondary action '" + wanted + "' failed on element " + index + " (" + role + "): " + Describe(ex));
            }
        }

        /// <summary>把输入的动作名规范化成七个标签之一；不认识返回 null。</summary>
        private static string NormalizeAction(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();
            string[] canon = { "Raise", "Scroll Up", "Scroll Down", "Scroll Left", "Scroll Right", "Expand", "Collapse" };
            foreach (string c in canon)
                if (string.Equals(c, s, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        private static string Describe(Exception ex)
        {
            System.Runtime.InteropServices.COMException ce =
                ex as System.Runtime.InteropServices.COMException;
            if (ce != null)
                return "COMException(0x" + ce.ErrorCode.ToString("X8") + "): " + ce.Message;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static int OkJson(IntPtr hwnd, int index, string role, string via, string value)
        {
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("index").Num(index)
                .Key("role").Val(role)
                .Key("via").Val(via)
                .Key("value").Val(value)
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        private static int FailJson(string code, string message)
        {
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(false)
                .Key("error").Val(code)
                .Key("message").Val(message)
             .End();
            Console.WriteLine(j.ToString());
            return 1;
        }

        private static int CmdType(string[] args)
        {
            RequireNotHalted("type");
            if (args.Length < 2) throw new ArgumentException("type 需要文本参数");
            string text = args[1];

            foreach (char c in text)
            {
                Native.keybd_event(0, (byte)c, Native.KEYEVENTF_UNICODE, UIntPtr.Zero);
                Native.keybd_event(0, (byte)c, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                System.Threading.Thread.Sleep(12);
            }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true).Key("typed").Num(text.Length).Key("text").Val(text).End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 发送组合键，形如 Ctrl+S、Alt+Tab、Enter。
        ///
        /// 先把所有修饰键按下，再按主键，然后逆序抬起——这个顺序是必须的，
        /// 打乱会让应用把 Ctrl+S 读成普通的 s。
        /// </summary>
        private static int CmdPressKey(string[] args)
        {
            RequireNotHalted("press_key");
            if (args.Length < 2) throw new ArgumentException("press_key 需要按键参数");
            string[] parts = args[1].Split('+');
            List<byte> mods = new List<byte>();
            byte main = 0;

            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                string low = p.ToLowerInvariant();
                byte vk;

                if (low == "ctrl" || low == "control") vk = 0x11;
                else if (low == "alt") vk = 0x12;
                else if (low == "shift") vk = 0x10;
                else if (low == "win") vk = 0x5B;
                else if (low == "enter" || low == "return") vk = 0x0D;
                else if (low == "tab") vk = 0x09;
                else if (low == "esc" || low == "escape") vk = 0x1B;
                else if (low == "space") vk = 0x20;
                else if (low == "backspace") vk = 0x08;
                else if (low == "delete" || low == "del") vk = 0x2E;
                else if (low == "up") vk = 0x26;
                else if (low == "down") vk = 0x28;
                else if (low == "left") vk = 0x25;
                else if (low == "right") vk = 0x27;
                else if (low == "home") vk = 0x24;
                else if (low == "end") vk = 0x23;
                else if (low == "f5") vk = 0x74;
                else if (p.Length == 1)
                {
                    char ch = char.ToUpperInvariant(p[0]);
                    vk = (byte)ch;
                }
                else throw new ArgumentException("不认识的按键：" + p);

                if (low == "ctrl" || low == "control" || low == "alt" || low == "shift" || low == "win")
                    mods.Add(vk);
                else
                    main = vk;
            }
            if (main == 0) throw new ArgumentException("只给了修饰键，缺少主键：" + args[1]);

            foreach (byte m in mods)
            {
                Native.keybd_event(m, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(20);
            }
            Native.keybd_event(main, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(40);
            Native.keybd_event(main, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                System.Threading.Thread.Sleep(20);
                Native.keybd_event(mods[i], 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true).Key("chord").Val(args[1]).End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 通过 SendInput 把光标移到绝对屏幕坐标。
        ///
        /// **不用 SetCursorPos。** 本机实测：同样坐标经 SetCursorPos 会被拒并报
        /// ERROR_FILE_NOT_FOUND(2) / ERROR_PATH_NOT_FOUND(3)，而 SendInput 的
        /// 绝对模式却成功（返回 1，光标真实移动）。SendInput 是更底层的输入
        /// 注入通道，因此改用它。
        ///
        /// 绝对坐标需归一化到 0..65535，并配合 MOUSEEVENTF_VIRTUALDESK
        /// 以覆盖多显示器构成的整个虚拟桌面。
        /// </summary>
        private static void MoveCursorAbsolute(int x, int y)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (vw <= 0) vw = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            if (vh <= 0) vh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            if (vw <= 0 || vh <= 0) throw new InvalidOperationException("无法取得屏幕尺寸");

            Native.INPUT[] inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].mi.dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
            inp[0].mi.dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            inp[0].mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;

            uint n = Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
            if (n == 0)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    "SendInput 移动光标失败 (Win32 错误 " + err + ": " +
                    new System.ComponentModel.Win32Exception(err).Message + ")。" +
                    "通常表示本进程没有交互式输入桌面的写入权限。");
            }
            System.Threading.Thread.Sleep(50);
        }

        /// <summary>发送一次鼠标按键（按下 + 抬起）。</summary>
        private static void SendMouseButton(bool right, int clickCount)
        {
            uint down = right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN;
            uint up = right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP;

            for (int i = 0; i < Math.Max(1, clickCount); i++)
            {
                Native.INPUT[] dn = new Native.INPUT[1];
                dn[0].type = Native.INPUT_MOUSE;
                dn[0].mi.dwFlags = down;
                Native.INPUT[] us = new Native.INPUT[1];
                us[0].type = Native.INPUT_MOUSE;
                us[0].mi.dwFlags = up;

                System.Threading.Thread.Sleep(40);
                if (Native.SendInput(1, dn, Marshal.SizeOf(typeof(Native.INPUT))) == 0)
                    throw new InvalidOperationException("SendInput 按下鼠标失败，err=" + Marshal.GetLastWin32Error());
                System.Threading.Thread.Sleep(40);
                if (Native.SendInput(1, us, Marshal.SizeOf(typeof(Native.INPUT))) == 0)
                    throw new InvalidOperationException("SendInput 抬起鼠标失败，err=" + Marshal.GetLastWin32Error());
            }
        }

        private static string WindowTitle(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(512);
            Native.GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string WindowClass(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            Native.GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static int CmdListWindows()
        {
            IntPtr fg = Native.GetForegroundWindow();
            List<IntPtr> handles = new List<IntPtr>();

            Native.EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                if (!Native.IsWindowVisible(h)) return true;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
                handles.Add(h);
                return true;
            }, IntPtr.Zero);

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true).Key("windows").Arr();
            foreach (IntPtr h in handles)
            {
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                string exe = ProcessPath(pid);

                j.Obj()
                    .Key("hwnd").Num(h.ToInt64())
                    .Key("title").Val(WindowTitle(h))
                    .Key("class").Val(WindowClass(h))
                    .Key("pid").Num(pid)
                    .Key("exe").Val(exe)
                    .Key("x").Num(r.Left).Key("y").Num(r.Top)
                    .Key("width").Num(r.Right - r.Left)
                    .Key("height").Num(r.Bottom - r.Top)
                    .Key("foreground").Bool(h == fg)
                 .End();
            }
            j.EndArr().End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>读取窗口的无障碍树，元素以稳定 id 标注，供后续动作寻址。</summary>
        private static int CmdTree(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("tree 需要 hwnd 参数");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            int maxNodes = args.Length > 2 ? int.Parse(args[2]) : 400;

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            // 树文本、索引表、次要动作表全部来自这一次遍历——它们必须同源，
            // 否则索引会和元素对不上号。
            Snapshot snap = SnapshotOps.Capture(root, hwnd, maxNodes);

            // 附加：Document/Edit 的文本内容，纯文本模型靠它判读界面
            StringBuilder extra = new StringBuilder();
            foreach (KeyValuePair<int, AutomationElement> kv in snap.Map)
            {
                string role = SnapshotOps.SafeStr(delegate { return kv.Value.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });
                if (role != "Document" && role != "Edit") continue;
                try
                {
                    object o;
                    if (kv.Value.TryGetCurrentPattern(TextPattern.Pattern, out o))
                    {
                        string t = ((TextPattern)o).DocumentRange.GetText(600);
                        if (!string.IsNullOrEmpty(t))
                            extra.Append("[").Append(kv.Key).Append("] text: ")
                                 .Append(SnapshotOps.Trim(t, 400)).Append('\n');
                    }
                }
                catch { }
            }

            string tree = snap.Tree;
            if (extra.Length > 0) tree = tree + "\n" + extra.ToString().TrimEnd();

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("revision").Num(snap.Revision)
                .Key("total").Num(snap.Total)
                .Key("listed").Num(snap.Listed)
                .Key("tree").Val(tree)
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        private static int _nodeCount;

        /// <summary>
        /// 遍历无障碍树。
        ///
        /// 用控件视图（ControlViewWalker）而不是原始视图：原始视图只暴露底层
        /// 实现结构，菜单、按钮、文本编辑器这些可交互元素都在控件视图里。
        /// </summary>
        private static string RenderNode(AutomationElement el, int depth, int maxNodes)
        {
            if (_nodeCount >= maxNodes) return "";
            _nodeCount++;

            string id = Register(el);
            string role = Safe(delegate { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });
            string name = Safe(delegate { return el.Current.Name; });
            string value = Safe(delegate
            {
                if (el.Current.IsPassword) return "<password>";
                object v = el.GetCurrentPropertyValue(ValuePattern.ValueProperty, true);
                return v as string;
            });

            Native.RECT r;
            GetElementRect(el, out r);

            StringBuilder sb = new StringBuilder();
            sb.Append(new string(' ', depth * 2));
            sb.Append("- ").Append(role);
            if (!string.IsNullOrEmpty(name)) sb.Append(" \"").Append(Trim(name, 90)).Append('"');
            if (!string.IsNullOrEmpty(value)) sb.Append(" = \"").Append(Trim(value, 70)).Append('"');
            sb.Append(" [").Append(id).Append(']');
            if (r.Right > r.Left)
                sb.Append(" @(").Append(r.Left).Append(',').Append(r.Top).Append(' ')
                  .Append(r.Right - r.Left).Append('x').Append(r.Bottom - r.Top).Append(')');
            sb.Append('\n');

            // 文本内容：纯文本模型靠它判读界面
            try
            {
                if (role == "Document" || role == "Edit" || role == "Text")
                {
                    string t = null;
                    object o;
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out o))
                    {
                        TextPattern tp = (TextPattern)o;
                        t = tp.DocumentRange.GetText(500);
                    }
                    if (string.IsNullOrEmpty(t)) t = name;
                    if (!string.IsNullOrEmpty(t) && t != name)
                        sb.Append(new string(' ', (depth + 1) * 2))
                          .Append("  text: ").Append(Trim(t, 320)).Append('\n');
                }
            }
            catch { }

            AutomationElement child = null;
            try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); } catch { }
            while (child != null && _nodeCount < maxNodes)
            {
                sb.Append(RenderNode(child, depth + 1, maxNodes));
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 把元素存入引用表并回一个短 id。
        ///
        /// 存引用而非重新查找，是为了绕开 PropertyCondition 的类型细节
        /// （dsh-click 正是在那里抛异常的）。短 id 只在本次会话内有效。
        /// </summary>
        private static string Register(AutomationElement el)
        {
            string id = "e" + _nextElementId++;
            Elements[id] = el;
            return id;
        }

        private static void GetElementRect(AutomationElement el, out Native.RECT r)
        {
            r = new Native.RECT();
            try
            {
                System.Windows.Rect b = el.Current.BoundingRectangle;
                if (!double.IsInfinity(b.Left) && !double.IsNaN(b.Left) && b.Width > 0 && b.Height > 0)
                {
                    r.Left = (int)b.Left; r.Top = (int)b.Top;
                    r.Right = (int)b.Right; r.Bottom = (int)b.Bottom;
                }
            }
            catch { }
        }

        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }

        private static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
