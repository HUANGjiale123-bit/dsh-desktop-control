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

        // ── DPI 状态的**读取**（区别于上面的设置）──
        //
        // 之前 ping 里直接输出 dpiAware=true，那是**写死的字面量**：
        // 既没看 SetProcessDPIAware 的返回值，也没读进程的真实状态。
        // 一个没被读过的值比不输出更糟——它让人以为已经确认过。
        //
        // GetThreadDpiAwarenessContext 需要 Win10 1703+，老系统上会抛
        // EntryPointNotFoundException，所以调用处必须兜住。
        [DllImport("user32.dll")]
        public static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

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

        /* ── 输入桌面绑定 ──
         * 本机实测：进程可能在"名字叫 Default、但不是当前输入桌面"的桌面上启动
         * （GetThreadDesktop=300 vs OpenInputDesktop=696），此时 SetCursorPos 报
         * Win32 错误 2、SendInput 静默无效、低层钩子永不回调——因为输入不在那个桌面上。
         * 修复：进程启动最早期 SetThreadDesktop(输入桌面)。
         * 顺序是硬要求：一旦本线程建过窗口或装过钩子，SetThreadDesktop 必然失败。 */
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")]
        public static extern IntPtr GetThreadDesktop(uint tid);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetThreadDesktop(IntPtr h);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        public const uint DESKTOP_READOBJECTS = 0x0001;
        public const uint DESKTOP_WRITEOBJECTS = 0x0080;
        public const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        public const uint GA_ROOT = 2;

        // 用于判断"这个窗口是不是目标的弹出窗口"（下拉菜单、提示、右键菜单）。
        // 弹窗的 GW_OWNER 指向它的宿主窗口，这是比"同一个进程"精确得多的判据。
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        public const uint GW_OWNER = 4;

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
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetCursorPos(int x, int y);

        /* mouse_event 是 SendInput 之前的经典注入 API。
         * 实测（用 Codex 的验证脚本在本机跑）：在失败环境里
         *   SendInput 相对移动 ret=1 但光标不动
         *   SetCursorPos            ret=True 且光标真的移动
         * 即 SendInput 被系统接受但不生效，而经典 API 正常。
         * 因此鼠标按键也改用 mouse_event，与 SetCursorPos 保持同一条路径。 */
        [DllImport("user32.dll", SetLastError = true)]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

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

        public const uint INPUT_KEYBOARD = 1;

        /// <summary>键盘事件的联合体成员。wScan 是 16 位，能装下任何 BMP 字符。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// SendInput 的 INPUT。**必须是显式布局的联合体**——
        /// 之前这里只有一个 MOUSEINPUT 字段，没法发键盘事件。
        /// 偏移量在 64 位下也成立（sizeof(INPUT)=40）。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
            [FieldOffset(8)] public KEYBDINPUT ki;
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
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        // 滚轮。dwData 用带符号的"格数 × WHEEL_DELTA"表达：
        //   正数 = 向上/远离用户（内容向上滚），负数 = 向下。
        // WHEEL_DELTA 固定 120，这是 Windows 的约定，不是本项目的选择。
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;
        public const int WHEEL_DELTA = 120;

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

        /// <summary>把字符串转义成合法的 JSON 字符串字面量（含引号）。</summary>
        public static string Str(string v) { return Quote(v); }

        /// <summary>原样插入一段已经是合法 JSON 的文本。</summary>
        public Jw Raw(string json) { Sep(); _sb.Append(json ?? "null"); return this; }

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
        /* 遍历上限的默认值。三个都是"防止在怪应用上挂死/一次抓回上千个活 RCW"的闸门，
         * 触发时会在树文本末尾明确报告被截断，而不是假装遍历完整。 */
        public const int DefaultMaxNodes = 400;
        public const int DefaultMaxDepth = 32;
        public const int DefaultMaxChildren = 200;

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

            // 闸门位置不能简单地从 exe 位置往上推。
            //
            // 实测：helper 部署到 %LOCALAPPDATA%\dsh-desktop-control\ 之后，
            // "往上两级"会算成 %LOCALAPPDATA%\.state\halt —— 在用户目录根下建了
            // 一个 .state 目录，既脏又不利于排查。
            //
            // 改为确定性优先级：
            //   1. DSC_HALT_GATE 环境变量（部署时指定，最可靠）
            //   2. 由 exe 位置推出的仓库根，**仅当该处确实有项目标记**时才用
            //   3. 否则退回 exe 自己所在目录（不污染上级）
            // 这样无论 helper 放在哪里，闸门位置都是可预测的。
            string binDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string root = Path.GetDirectoryName(binDir);
            if (!string.IsNullOrEmpty(root)) root = Path.GetDirectoryName(root);

            // 项目标记：仓库根下应当同时存在 helper 与 plugin 两个目录
            if (!string.IsNullOrEmpty(root) &&
                Directory.Exists(Path.Combine(root, "helper")) &&
                Directory.Exists(Path.Combine(root, "plugin")))
                return Path.Combine(root, ".state", "halt");

            // 插件安装后的情形：exe 在 <插件目录>\bin\ 下。
            //
            // 这里要判断"能不能写"，而不是直接用。别人从市场装插件时，
            // 插件目录可能落在 Program Files 之类的只读位置，那时写闸门会抛
            // UnauthorizedAccessException——**而闸门是急停机制，写不成就等于
            // 停不下来**。
            //
            // 正常情况下插件会显式传 DSC_HALT_GATE（走到本函数第一行就返回了），
            // 所以这条路径只在手敲 dsc.exe 时生效。但"只在手敲时生效的坏路径"
            // 同样是坏的，而且正好是使用者试用时会走的那条。
            string own = Path.Combine(binDir, ".state", "halt");
            if (CanWriteNear(own)) return own;

            // 退回每用户可写位置。%LOCALAPPDATA% 是放这类状态的正规去处。
            try
            {
                string la = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(la))
                    return Path.Combine(la, "dsh-desktop-control", "halt");
            }
            catch { }

            return own;   // 都不行就用原路径，让后续写入如实报错
        }

        /// <summary>
        /// 探测某个闸门文件所在目录能否创建文件。
        ///
        /// 真去建一个探测文件再删掉，而不是查 ACL——ACL 判断复杂且容易判错，
        /// 而"实际写一次"是唯一可靠的答案。用随机名避免和并发调用撞上。
        /// </summary>
        private static bool CanWriteNear(string gateFile)
        {
            try
            {
                string dir = Path.GetDirectoryName(gateFile);
                if (string.IsNullOrEmpty(dir)) return false;
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
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
        /* ── AppContainer 自检 ──
         *
         * AppContainer 进程被 Windows 禁止向交互式桌面注入输入。症状是**静默的**：
         * SendInput 返回成功、SetCursorPos 可能返回 False、光标不动、**没有任何报错**。
         *
         * 本项目已经吃过一次这个亏：把一次观察推广成"本环境阻断输入注入"，
         * 写进 README，结果把两个真 bug 都"解释"掉了。
         * 所以启动时直接查自己的令牌，是 AppContainer 就明确报错退出，
         * 别让同样的静默失败再伪装成环境限制。
         *
         * 依据：Codex 复核（2026-10-14）——它在自己的环境里测得 IsAppContainer=1，
         * 并判定这是注入无效的最可能根因（它标注为最强假设而非已证因果）。
         */
        private static bool? _isAppContainer;

        // OpenProcessToken / GetTokenInformation 的声明在下面完整性检查那一段，
        // 这里复用，不重复声明（C# 会报 CS0111 重复成员）。

        private static bool IsAppContainer()
        {
            if (_isAppContainer.HasValue) return _isAppContainer.Value;
            _isAppContainer = false;
            IntPtr tok = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                                      TOKEN_QUERY, out tok)) return false;
                buf = Marshal.AllocHGlobal(64);
                int ret;
                if (GetTokenInformation(tok, 29 /*TokenIsAppContainer*/, buf, 64, out ret))
                    _isAppContainer = Marshal.ReadInt32(buf) != 0;
            }
            catch { }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (tok != IntPtr.Zero) Native.CloseHandle(tok);
            }
            return _isAppContainer.Value;
        }

        /// <summary>
        /// 在 AppContainer 里就拒绝执行注入类命令。
        /// 注意：读类命令（list_windows / tree / screenshot）不受影响，它们不需要注入。
        /// </summary>
        private static void RequireNotAppContainer(string action)
        {
            if (!IsAppContainer()) return;
            throw new InvalidOperationException(
                "本进程运行在 AppContainer（沙箱）里，Windows 禁止由此向交互式桌面注入输入，拒绝执行 " +
                action + "。症状是静默的：SendInput 会返回成功但光标不动、按键不进任何程序。" +
                "常见原因：helper 是从 Codex/ChatGPT 会话的终端启动的，继承了它的沙箱。" +
                "解决办法：从普通的命令提示符或快捷方式启动，不要从沙箱会话里启动。" +
                "（观察类命令不受此限制。）");
        }

        private static void RequireNotHalted(string action)
        {
            if (!IsHalted()) return;
            throw new DscError("Halted", 
                "已被急停：闸门文件存在（" + GatePath() + "），拒绝执行 " + action + "。\n" +
                "这不是故障，是用户按了 ESC 叫停。\n" +
                "**不要重试** —— 重试不会成功，只会让指示层反复冒出来，用户看起来像是插件不听他的。\n" +
                "也不要自行解除：急停是用户发起的，该由用户结束（删掉 " + GatePath() + " 即可）。\n" +
                "现在该做的是：停下，把情况告诉用户，等他决定。");
        }

        /* ═══════════════ 应用白名单闸门 ═══════════════
         *
         * 这道闸门在 helper 里，而不是只在插件层。
         *
         * 为什么必须下沉到这里：helper 是磁盘上一个可执行文件，任何人都能直接运行它。
         * 白名单只做在插件层时，绕过插件就等于绕过全部限制。在输入注入还被误认为
         * 不可用的那段时间这个缺口影响有限；注入可用之后，它意味着 dsc.exe 可以直接
         * 操作这台机器上的任何窗口。
         *
         * 默认拒绝：白名单为空时，所有变更性操作一律拒绝。
         * 读取类（list_windows / tree / screenshot / ping）不受限制——它们不改状态。
         */

        /// <summary>白名单：小写可执行文件名。来源是 --allow 参数或 DSC_ALLOWED_APPS 环境变量。</summary>
        private static string[] _allowlist;

        /* ── 关于「绑定输入桌面」的实测记录（结论：不要做）──
         *
         * 曾观察到 GetThreadDesktop(tid)=328 与 OpenInputDesktop()=704 句柄不同，
         * 据此推测进程在错误的桌面上，于是加了 BindInputDesktop() ——
         * 在任何 COM/UIA 调用之前 SetThreadDesktop(输入桌面)。
         *
         * 实测（dsc-bindverify.exe）：
         *   绑定前  SendInput 返回 1（被接受），光标位移 (0,0)   ← 无效但被接受
         *   绑定后  SendInput 返回 0，err=5 拒绝访问            ← 更糟
         *
         * 所以绑定不是解药，反而有害，已移除。
         * 另一个独立事实：句柄值不同**不能**推出桌面对象不同（同一桌面可以有两个句柄），
         * 当时把它当成了证据，这是推断上的错误。
         */

        private static string[] Allowlist()
        {
            if (_allowlist == null)
            {
                string raw = Environment.GetEnvironmentVariable("DSC_ALLOWED_APPS");
                for (int i = 0; i < _argsRaw.Length - 1; i++)
                    if (_argsRaw[i] == "--allow") raw = _argsRaw[i + 1];

                if (string.IsNullOrEmpty(raw)) _allowlist = new string[0];
                else
                {
                    string[] parts = raw.Split(',');
                    List<string> keep = new List<string>();
                    foreach (string x in parts)
                    {
                        string t = x.Trim().ToLowerInvariant();
                        if (t.Length > 0) keep.Add(t);
                    }
                    _allowlist = keep.ToArray();
                }
            }
            return _allowlist;
        }

        private static string[] _argsRaw = new string[0];

        /// <summary>从完整路径取出小写文件名。路径分隔符两种都处理。</summary>
        private static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOf('\\');
            int j = path.LastIndexOf('/');
            int k = Math.Max(i, j);
            return (k >= 0 ? path.Substring(k + 1) : path).ToLowerInvariant();
        }

        /// <summary>
        /// 从参数里解析窗口句柄，并给出**可读的**错误。
        ///
        /// 原先各处直接写 `long.Parse(args[1])`，参数缺失或为空时抛的是
        /// `FormatException: 输入字符串的格式不正确。` —— 调用方（或手敲命令的人）
        /// 从这句话里看不出是"没给 hwnd"还是"hwnd 写得不对"，更不知道该填什么。
        ///
        /// 句柄来自列表命令（list_windows / read_window 输出里的 hwnd=NNNN）。
        /// </summary>
        private static IntPtr ParseHwnd(string[] args, string action)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                throw new ArgumentException(
                    action + " 需要一个窗口句柄。用法：" + action + " <hwnd> [其它参数]。" +
                    "句柄取自 list_windows 输出里的 hwnd=NNNN。");

            long v;
            if (!long.TryParse(args[1].Trim(), out v) || v == 0)
                throw new ArgumentException(
                    action + " 的窗口句柄无法解析：" + args[1].Trim() +
                    "。它应当是一个十进制整数，取自 list_windows 输出里的 hwnd=NNNN。");

            return new IntPtr(v);
        }

        /// <summary>
        /// 校验某个窗口所属的应用是否在白名单内。不在就抛异常。
        ///
        /// 三道判断，任何一道不过都拒绝：
        ///   1. 白名单为空        → 什么都别做
        ///   2. 取不到可执行文件  → 无法校验，拒绝（宁可拒绝也不放行未知目标）
        ///   3. 不在名单内        → 拒绝并列出当前名单
        ///
        /// ★ 观察类命令（tree / screenshot）**同样适用**。
        ///
        /// 这里原先写着"观察类命令不受限制"，那是早期的设计：读界面被当作无害。
        /// 后来改了——**读无障碍树会拿到界面上全部文字**（文档正文、聊天记录、
        /// 表单里已填的值），截图就是界面本身，所以插件层把观察也纳入了授权。
        /// 但这句话和 tree/screenshot 里缺失的调用没跟着改，于是两头不一致：
        /// 插件层要授权，而绕过插件直接调 helper 时**没有任何校验**。
        ///
        /// 现在两边一致：读界面内容同样要过白名单。纵深防御——插件层的授权
        /// 是主闸门，这里是第二道；任一被绕过都不至于全开。
        /// </summary>
        private static void RequireAllowedWindow(IntPtr hwnd, string action)
        {
            string[] allow = Allowlist();
            if (allow.Length == 0)
                throw new InvalidOperationException(
                    "白名单为空，拒绝执行 " + action + "。设置 DSC_ALLOWED_APPS（逗号分隔的可执行文件名，" +
                    "例如 notepad.exe）或用 --allow notepad.exe 指定后再试。" +
                    "读取界面内容同样需要授权——观察也是访问。");

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0)
                throw new InvalidOperationException("无法确定窗口 " + hwnd + " 所属进程，拒绝执行 " + action + "。");

            string exe = BaseName(ProcessPath(pid));
            if (exe.Length == 0)
                throw new InvalidOperationException(
                    "无法确定窗口 " + hwnd + " 所属的可执行文件，拒绝执行 " + action + "（无法校验白名单）。");

            foreach (string a in allow)
                if (a == exe) return;

            throw new InvalidOperationException(
                "目标应用 \"" + exe + "\" 不在白名单内，拒绝执行 " + action + "。当前白名单：" +
                string.Join(", ", allow) + "。");
        }

        /* ── 完整性级别检查 ──
         * 照 Codex 的做法：读目标进程令牌的完整性级别，与自身比较。
         * 目标更高时 Codex 的文案是：
         *   "Accessibility is limited because the target window has higher Windows
         *    integrity than the Computer Use helper."
         * 用户取证的结论是：不存在"读受限但写仍可"，应按**基本不可用**处理，
         * 检测到就报错交还用户，而不是硬试。
         */
        private static int _selfIntegrityRid = -1;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr t, int cls, IntPtr info, int len, out int ret);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSidSubAuthorityCount(IntPtr sid, out byte count);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        private const uint TOKEN_QUERY = 0x0008;

        /// <summary>读进程令牌的完整性级别 RID；读不到返回 -1。</summary>
        private static int IntegrityRid(uint pid)
        {
            IntPtr proc = IntPtr.Zero, tok = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                proc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (proc == IntPtr.Zero) return -1;
                if (!OpenProcessToken(proc, TOKEN_QUERY, out tok)) return -1;
                buf = Marshal.AllocHGlobal(512);
                int ret;
                if (!GetTokenInformation(tok, 25 /*TokenIntegrityLevel*/, buf, 512, out ret)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte cnt;
                if (!GetSidSubAuthorityCount(sid, out cnt) || cnt == 0) return -1;
                IntPtr pSub = GetSidSubAuthority(sid, (uint)(cnt - 1));
                return Marshal.ReadInt32(pSub);
            }
            catch { return -1; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (tok != IntPtr.Zero) Native.CloseHandle(tok);
                if (proc != IntPtr.Zero) Native.CloseHandle(proc);
            }
        }

        private static string IntegrityName(int rid)
        {
            if (rid < 0) return "未知";
            if (rid < 0x1000) return "Untrusted";
            if (rid < 0x2000) return "Low";
            if (rid < 0x3000) return "Medium";
            if (rid < 0x4000) return "Medium-Plus";
            if (rid < 0x5000) return "High(管理员)";
            return "System";
        }

        /// <summary>
        /// 目标完整性高于自身时拒绝——照 Codex 的处理方式。
        /// 不做"读受限但写仍可"的尝试，因为那种情况不存在。
        /// </summary>
        private static void RequireIntegrityOk(IntPtr hwnd, string action)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return;

            if (_selfIntegrityRid < 0)
            {
                uint self;
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out self);  // 仅用于占位
                _selfIntegrityRid = IntegrityRid((uint)System.Diagnostics.Process.GetCurrentProcess().Id);
            }

            int target = IntegrityRid(pid);
            if (target < 0 || _selfIntegrityRid < 0) return;   // 读不到就不阻断

            if (target > _selfIntegrityRid)
                throw new InvalidOperationException(
                    "Accessibility is limited because the target window has higher Windows " +
                    "integrity than this helper. 目标完整性=" + IntegrityName(target) +
                    "，自身=" + IntegrityName(_selfIntegrityRid) +
                    "。按已验证的结论，这种情况不存在\"读受限但写仍可\"，因此拒绝执行 " + action + "。");
        }

        /// <summary>
        /// 把目标窗口提到前台并把焦点交给它，然后校验确实到位了。
        ///
        /// 必要性：type / press_key 经由 SendInput 发到**当前焦点窗口**。
        /// 焦点跑偏时文本会打进别的程序——那既是错误操作，也可能泄露内容。
        /// 所以先确认焦点在目标窗口上，不确认就不发。
        /// </summary>
        /// <summary>
        /// 注入前把目标窗口激活到前台，失败即拒绝。
        ///
        /// 为什么是"拒绝"而不是"尽力而为"：实测在非前台状态下注入会进入
        /// 合成输入被系统压制的状态——SendInput 返回成功、光标不动、没有报错。
        /// 那比明确失败危险得多。Codex 的做法也是先激活，激活是输入的前置条件。
        ///
        /// 覆盖注：本方法取代了原先只做"检查 + 一次性尝试"的 RequireFocus。
        /// 区别在于 RequireFocus 不还原最小化窗口、不用 AttachThreadInput，
        /// 也不在失败时说明清楚为什么不继续。
        /// </summary>
        private static void RequireActivated(IntPtr hwnd, string action)
        {
            RequireNotAppContainer(action);
            RequireIntegrityOk(hwnd, action);

            WindowActivator.Result r = WindowActivator.Activate(hwnd);
            if (r.Ok) return;
            throw new InvalidOperationException("拒绝执行 " + action + "：" + r.Message);
        }

        /// <summary>本进程内的元素引用表：短 id → 无障碍元素。</summary>
        private static readonly Dictionary<string, AutomationElement> Elements =
            new Dictionary<string, AutomationElement>(StringComparer.Ordinal);

        private static int _nextElementId = 1;

        /// <summary>SetProcessDPIAware() 的返回值（true=调用被接受）。</summary>
        private static bool _dpiAwareCallOk;

        /// <summary>读回来的实际 DPI 感知级别；-1 表示没读到。</summary>
        private static int _dpiAwareness = -1;

        /// <summary>把 DPI 感知级别转成可读名字。</summary>
        private static string DpiAwarenessName()
        {
            switch (_dpiAwareness)
            {
                case 0: return "UNAWARE";
                case 1: return "SYSTEM_AWARE";
                case 2: return "PER_MONITOR_AWARE";
                case -1: return "UNREAD";
                default: return "UNKNOWN(" + _dpiAwareness + ")";
            }
        }

        private static int Main(string[] args)
        {
            _argsRaw = args;

            // ★ 必须先于任何窗口/坐标 API。
            // 同时把**返回值**和**实际状态**都记下来，供 ping 如实报告。
            _dpiAwareCallOk = Native.SetProcessDPIAware();
            try
            {
                IntPtr ctx = Native.GetThreadDpiAwarenessContext();
                _dpiAwareness = Native.GetAwarenessFromDpiAwarenessContext(ctx);
            }
            catch { _dpiAwareness = -1; }

            // 注：曾在此处调用 BindInputDesktop() 想切到当前输入桌面。
            // 实测撤销：SetThreadDesktop 确实成功（句柄从 328 变成 704），
            // 但注入反而从"返回成功但无效"退化成"返回 0 + 拒绝访问(5)"。
            // 即"修复"比不修更糟，故移除。桌面句柄不同不构成本次注入失败的原因。
            Console.OutputEncoding = Encoding.UTF8;

            // 剥掉全局选项（--allow x），它们可能出现在命令名之前。
            // 不剥的话 `dsc.exe --allow notepad.exe set_value ...` 会被当成 help
            // ——这个坑我踩过一次，测试时看到的是帮助文本而不是错误。
            List<string> rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--allow" && i + 1 < args.Length) { i++; continue; }
                rest.Add(args[i]);
            }
            args = rest.ToArray();

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
                    case "move": return CmdMove(args);
                    case "drag": return CmdDrag(args);
                    case "wheel": return CmdWheel(args);
                    case "whatis": return CmdWhatIs(args);
                    case "hold": return CmdHold(args);
                    case "release": return CmdRelease(args);
                    case "dragpath": return CmdDragPath(args);
                    case "set_value": return CmdSetValue(args);
                    case "do_action": return CmdDoAction(args);
                    case "type": return CmdType(args);
                    case "press_key": return CmdPressKey(args);
                    case "halt": return CmdHalt(args);
                    case "stop": return CmdStop(args);
                    case "ping": return CmdPing();
                    case "allowcheck": return CmdAllowCheck(args);
                    case "gate_probe": return CmdGateProbe();
                    case "list_apps": return CmdListApps(args);
                    case "resolve_app": return CmdResolveApp(args);
                    case "launch": return CmdLaunch(args);
                    default: return CmdHelp();
                }
            }
            catch (Exception ex)
            {
                Jw e = new Jw();
                // 错误码优先取异常自带的。类名只在没有码时兜底 ——
                // "InvalidOperationException" 这种对调用方毫无意义。
                // C# 5：没有 null-conditional 运算符
                DscError dscEx = ex as DscError;
                string code = dscEx != null ? dscEx.Code : ex.GetType().Name;
                e.Obj().Key("ok").Bool(false)
                    .Key("error").Val(code)
                    .Key("message").Val(ex.Message)
                 .End();
                Console.WriteLine(e.ToString());
                return 1;
            }
        }

        private static int CmdHelp()
        {
            Console.WriteLine(@"dsh-desktop-control helper（一般由插件调用，不直接手敲）

── 应用清单与启动 ──
  dsc.exe list_apps [关键词] [--limit N]   列出已安装应用
  dsc.exe resolve_app <名字>               只解析名字到 exe，不启动
  dsc.exe launch <名字或 exe 路径>         启动（只接受清单里的名字或明确路径）

── 观察（读界面内容需先经插件授权）──
  dsc.exe list_windows                     列窗口（不含标题）
  dsc.exe tree <hwnd> [maxNodes]           读无障碍树 + 质量度量
  dsc.exe screenshot <hwnd> [outPath]      截图

── 急停闸门 ──
  dsc.exe halt [原因]                      拉下闸门（急停；此后所有操作被拒）
  dsc.exe stop                             查看闸门状态（**只读，不改任何文件**）
  dsc.exe stop --clear                     解除闸门
  dsc.exe stop --open                      同 --clear

── 诊断 ──
  dsc.exe ping                             存活 + DPI + 闸门路径
  dsc.exe allowcheck <hwnd>                该窗口是否通过白名单

环境变量 DSC_HALT_GATE 可指定闸门文件位置。未指定时按 exe 位置推导。");
            return 0;
        }

        /// <summary>诊断：打印白名单闸门实际看到的值。用来查"为什么明明在白名单里却被拒"。</summary>
        private static int CmdAllowCheck(string[] args)
        {
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            string full = ProcessPath(pid);
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("pid").Num(pid)
                .Key("fullPath").Val(full)
                .Key("baseName").Val(BaseName(full))
                .Key("allowlist").Arr();
            foreach (string a in Allowlist()) j.Val(a);
            j.EndArr().End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        private static int CmdPing()
        {
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                // dpiAware 现在是**读出来的**，不是写死的。
                // 另附可读级别与初始化调用的结果——三者合起来才能判断
                // "坐标为什么可能偏移"，只给一个 true 什么也说明不了。
                .Key("dpiAware").Bool(_dpiAwareness > 0)
                .Key("dpiAwareness").Val(DpiAwarenessName())
                .Key("dpiInitOk").Bool(_dpiAwareCallOk)
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
        /// <summary>
        /// 带**错误码**的异常。
        ///
        /// 为什么需要它：Main 的兜底 catch 一直用 `ex.GetType().Name` 当错误码，
        /// 于是 RequireNotHalted 抛出来的是 "InvalidOperationException"。
        /// 结果是插件侧的错误建议表查不到 Halted，退化成"未知 → 重新观察"，
        /// 而那条消息里明明写着"不要重试" —— 自相矛盾。
        ///
        /// 实测发现的：按 ESC 之后调 read_window，返回的建议是 reobserve。
        /// 所以错误码必须是**抛的人声明的事实**，不能是异常类名。
        /// </summary>
        private class DscError : Exception
        {
            public readonly string Code;
            public DscError(string code, string message) : base(message) { Code = code; }
        }

        private static int CmdHalt(string[] args)
        {
            // 解析 --reason <值>，同时兼容直接给位置参数。
            //
            // 上一版直接取 args[1]，于是 `halt --reason "用户按了 ESC"`
            // 把字面的 "--reason" 写进了闸门文件，而真正的原因丢了。
            // 闸门内容后来会被 diagnose 显示成"为什么被停"，
            // 所以这不只是丑，它让事后追查失真。
            string reason = "由调用方触发";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--reason")
                {
                    if (i + 1 < args.Length) reason = args[++i];
                }
                else if (!args[i].StartsWith("--"))
                {
                    reason = args[i];
                }
            }
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

        /// <summary>
        /// 实际探测闸门文件能不能写、能不能删。用法：dsc.exe gate_probe
        ///
        /// **为什么需要它**：插件原先判断"闸门位置对不对"用的是路径名启发式
        /// （看路径里有没有含某个固定字样），那是**写死某台机器的目录名**。
        /// 换一个工作区就永远匹配不上，于是正常情况被报成
        /// "★ 闸门位置不正确 —— ESC 会只关界面而不拦住操作"。
        ///
        /// 这个误报的代价不小：它让使用者以为急停不可靠，而实际是好的。
        /// 而真正要回答的问题从来不是"路径长什么样"，而是
        /// **"这道闸门拦得住操作吗"** —— 那就实际做一次：
        ///   写闸门 → 查它是否被认定为已拉下 → 删掉 → 查是否恢复
        /// 三步都对才算通过。这是功能性判断，和目录名无关。
        /// </summary>
        /// <summary>
        /// 探测闸门路径是否可用：能写、能读回、能删掉。
        ///
        /// ── 绝不能拿真实闸门做这个测试 ──
        ///
        /// 上一版直接用 GatePath() 写、再删。问题是：**闸门此刻可能正拉下**
        /// （用户按了 ESC 在急停）。那样这个"诊断"会
        ///   1. 把用户按 ESC 的原因覆盖成 "gate_probe"
        ///   2. 然后把闸门删掉 —— 用户的急停被静默解除
        /// 而且它还会报告 healthy。`desktop_diagnose` 每调一次就解除一次急停。
        ///
        /// 所以分两种情况：
        ///   · 闸门**不存在**：可以放心用真实路径做写→查→删，
        ///     因为这正是要验证的"写入与读取是同一个路径"。
        ///   · 闸门**存在**：绝不动它。改用同目录的临时探测文件验证
        ///     "这个目录可写"，然后如实报告"闸门当前处于拉下状态"。
        /// </summary>
        private static int CmdGateProbe()
        {
            string p = GatePath();
            bool alreadyHalted = IsHalted();

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true).Key("gatePath").Val(p)
                .Key("alreadyHalted").Bool(alreadyHalted);

            if (alreadyHalted)
            {
                // ── 闸门正拉下：只测目录可写，不碰闸门 ──
                string probe = p + ".probe-" + System.Diagnostics.Process.GetCurrentProcess().Id;
                try
                {
                    File.WriteAllText(probe, "probe");
                    bool wrote = File.Exists(probe);
                    File.Delete(probe);
                    bool gone = !File.Exists(probe);
                    j.Key("writable").Bool(wrote && gone)
                     .Key("stage").Val("probe-only")
                     .Key("note").Val(
                         "闸门当前处于拉下状态，**没有动它**——只用同目录的临时文件" +
                         "验证了目录可写。用户按 ESC 造成的急停保持有效。");
                    if (!(wrote && gone))
                        j.Key("error").Val("临时探测文件写不进去或删不掉");
                    j.End();
                    Console.WriteLine(j.ToString());
                    return wrote && gone ? 0 : 3;
                }
                catch (Exception e)
                {
                    j.Key("writable").Bool(false)
                     .Key("stage").Val("probe-only")
                     .Key("error").Val(e.GetType().Name + ": " + e.Message)
                     .End();
                    Console.WriteLine(j.ToString());
                    return 3;
                }
            }

            // ── 闸门不存在：可以用真实路径做完整测试 ──
            try
            {
                EnsureHalt("gate_probe");
            }
            catch (Exception e)
            {
                j.Key("writable").Bool(false)
                 .Key("stage").Val("write")
                 .Key("error").Val(e.GetType().Name + ": " + e.Message)
                 .End();
                Console.WriteLine(j.ToString());
                return 3;
            }

            bool halted = IsHalted();
            if (!halted)
            {
                j.Key("writable").Bool(true)
                 .Key("halted").Bool(false)
                 .Key("stage").Val("verify")
                 .Key("error").Val("闸门文件写成了，但 IsHalted() 不认为已拉下——写入与读取不是同一个路径")
                 .End();
                Console.WriteLine(j.ToString());
                return 4;
            }

            try
            {
                File.Delete(p);
            }
            catch (Exception e)
            {
                j.Key("writable").Bool(true).Key("halted").Bool(true)
                 .Key("stage").Val("clear")
                 .Key("error").Val(e.GetType().Name + ": " + e.Message)
                 .End();
                Console.WriteLine(j.ToString());
                return 5;
            }

            bool cleared = !IsHalted();
            j.Key("writable").Bool(true)
             .Key("halted").Bool(true)
             .Key("clearable").Bool(cleared)
             .Key("healthy").Bool(cleared)
             .Key("note").Val("闸门原本不存在，用真实路径做了完整测试并已清理");
            if (!cleared)
                j.Key("error").Val("闸门删掉了，但 IsHalted() 仍认为已拉下");
            j.End();
            Console.WriteLine(j.ToString());
            return cleared ? 0 : 6;
        }

        /// <summary>
        /// 列出已安装的应用。用法：dsc.exe list_apps [关键词] [--limit N]
        ///
        /// launch 的配套：**必须先能看到清单，才谈得上"选择要启动哪个"**。
        /// 清单只取注册表里声明了显示名的程序，不扫开始菜单快捷方式——
        /// 后者会把卸载入口、帮助文档也算进来，清单就没法用了。
        /// </summary>
        private static int CmdListApps(string[] args)
        {
            string query = "";
            int limit = 200;
            // ★ 从 1 开始，跳过 args[0] —— 那是命令名本身。
            //
            // 踩过的坑：Main 收到的 args 包含命令名，所以从 0 开始遍历会把
            // "list_apps" 当成搜索关键词，于是不带参数时去搜"list_apps"、
            // 返回 0 条；而带关键词时正好被覆盖，看起来又是对的。
            // 这种"只在某一个用法下出错"的 bug 最难看出来。
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--limit")
                {
                    // 把随后的数字当作参数值吃掉，否则它会被当成搜索关键词。
                    if (i + 1 < args.Length) int.TryParse(args[++i], out limit);
                    continue;
                }
                if (args[i].Length > 0 && args[i][0] != '-') query = args[i];
            }

            // 一律取【全部匹配项】，再统一在下面按 limit 截断。
            //
            // 早先这里是 `Search(query, limit)` 配一个默认 200：于是 limit 传 500
            // 时 Search 只给到默认上限，而截断判断用的是 500 —— 结果 truncated=false
            // 却少了条目，是个自相矛盾的输出。两处用同一个数才不会有这种缝。
            List<Launcher.AppEntry> all =
                string.IsNullOrEmpty(query) ? Launcher.List() : Launcher.Search(query, int.MaxValue);

            // 按 limit 截断，并**同时报出截断前后的数量**。
            //
            // 原先只报 total（= 全部数量），而 apps 数组是按 limit 截断过的。
            // 于是 `--limit 3` 返回 total:80 配 3 个条目：调用方若用 apps.length
            // 显示"已安装应用 3 个"，看不出被截断；若用 total 显示，又和列表对不上。
            // 两个数都给，调用方才能说实话。
            int available = all.Count;
            List<Launcher.AppEntry> shown = new List<Launcher.AppEntry>();
            foreach (Launcher.AppEntry a in all)
            {
                if (shown.Count >= limit) break;
                shown.Add(a);
            }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("total").Num(available)
                .Key("returned").Num(shown.Count)
                .Key("truncated").Bool(shown.Count < available);
            j.Key("apps").Arr();
            foreach (Launcher.AppEntry a in shown)
            {
                j.Obj().Key("name").Val(a.Name).Key("exe").Val(a.Exe).Key("version").Val(a.Version).End();
            }
            j.EndArr().End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 启动一个已安装的应用。用法：dsc.exe launch &lt;名字或路径&gt;
        ///
        /// **安全边界**：只接受两种目标——
        ///   ① 应用清单里的显示名或 exe 文件名
        ///   ② 调用方明确给出的 .exe 完整路径
        /// **不接受任意命令行**，否则这个接口等同于"以用户权限执行任意程序"，
        /// 比不提供它危险得多。
        ///
        /// 为什么它必须存在：插件原来只能操作**已经打开的**窗口，
        /// "打开应用"只能从 shell 做，而 shell 不归插件白名单管——
        /// 结果是**授权顺序整个反了**：应用先被打开（未经检查），
        /// 然后才问"能不能操作它"。有了 launch，正确顺序才成立：
        /// 授权 → 启动 → 操作。
        /// </summary>
        /// <summary>
        /// 只解析应用名到可执行文件，**不启动**。用法：dsc.exe resolve_app &lt;名字&gt;
        ///
        /// 为什么必须把"解析"和"启动"分开：
        /// 授权必须在启动**之前**发生。如果只有一个既解析又启动的命令，
        /// 调用方就没机会在启动前问"允许吗"——只能先启动、再补问，
        /// 那正是要修的授权顺序问题。
        ///
        /// 所以拆成两步：resolve_app（查，不动）→ 调用方授权 → launch（动）。
        /// </summary>
        private static int CmdResolveApp(string[] args)
        {
            string target = "";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Length > 0 && args[i][0] != '-') { target = args[i]; break; }
            }

            if (string.IsNullOrEmpty(target))
            {
                Jw bad = new Jw();
                bad.Obj().Key("ok").Bool(false).Key("error").Val("BadArgs")
                    .Key("message").Val("用法：dsc.exe resolve_app <应用名>").End();
                Console.WriteLine(bad.ToString());
                return 2;
            }

            // 只有"exe 完整路径"这一种目标不走清单
            string exe = Launcher.Resolve(target);

            Jw j = new Jw();
            if (string.IsNullOrEmpty(exe))
            {
                // 两种失败要说清是哪一种。
                //
                // 原先两种情况共用一句"没有唯一解析到可执行文件"：
                //   · 一个都没匹配上（用户打了清单里没有的名字）
                //   · 匹配到多个，无法确定是哪一个
                // 前者该说"没这个名字，去看清单"，后者该说"可能是这几个"。
                // 都报成后者，用户会去猜"到底哪几个重名了"——白花时间。
                //
                // 实测触发：问"记事本"（清单里叫 Notepad），得到的是
                // "没有唯一解析到"配一个空候选列表，读起来像系统出错。
                List<Launcher.AppEntry> cands = Launcher.Search(target, 8);
                bool noneMatched = cands.Count == 0;

                j.Obj().Key("ok").Bool(false)
                    .Key("error").Val(noneMatched ? "NoMatch" : "Ambiguous");
                j.Key("message").Val(noneMatched
                    ? "清单里没有任何应用匹配\"" + target + "\"。请用 desktop_list_apps 查看准确的名字" +
                      "（清单里的名字多为英文或安装时登记的显示名，未必和中文俗称一致）。"
                    : "匹配到多个应用，无法确定要启动哪一个，拒绝启动（宁可不动，也不要启错程序）。" +
                      "请用更准确的名字，或直接用 .exe 完整路径。");
                j.Key("candidates").Arr();
                foreach (Launcher.AppEntry a in cands)
                    j.Obj().Key("name").Val(a.Name).Key("exe").Val(a.Exe).End();
                j.EndArr().End();
                Console.WriteLine(j.ToString());
                return 3;
            }

            // 顺便给出"这个 exe 属于哪个应用名"——授权时要显示给用户看
            string displayName = target;
            foreach (Launcher.AppEntry a in Launcher.List())
            {
                if (string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase))
                {
                    displayName = a.Name;
                    break;
                }
            }

            j.Obj().Key("ok").Bool(true).Key("exe").Val(exe).Key("name").Val(displayName).End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        private static int CmdLaunch(string[] args)
        {
            string target = "";
            // ★ 从 1 开始：args[0] 是命令名 "launch"，不是目标。
            // 从 0 开始会把 "launch" 当应用名去解析，然后报"找不到"。
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Length > 0 && args[i][0] != '-') { target = args[i]; break; }
            }

            if (string.IsNullOrEmpty(target))
            {
                Jw bad = new Jw();
                bad.Obj().Key("ok").Bool(false)
                    .Key("error").Val("BadArgs")
                    .Key("message").Val("用法：dsc.exe launch <应用名或 .exe 路径>。先用 list_apps 看清单。")
                 .End();
                Console.WriteLine(bad.ToString());
                return 2;
            }

            // 启动程序也是变更性操作，受闸门约束。
            //
            // 漏掉它的后果实测过：闸门拉下后 launch 照样把记事本启动起来
            // （ok:true、进程数从 0 变成 3）。插件层虽然会拦，但急停
            // 的含义是别再动我的电脑，不该只在某一层成立。
            RequireNotHalted("launch");

            string exe = Launcher.Resolve(target);
            if (string.IsNullOrEmpty(exe))
            {
                // 没解析到就给候选，而不是只说"没找到"——
                // 使用者需要知道的是"这个名字对应哪些真实存在的应用"。
                List<Launcher.AppEntry> cands = Launcher.Search(target, 8);
                bool noneMatched = cands.Count == 0;
                Jw j = new Jw();
                j.Obj().Key("ok").Bool(false)
                    .Key("error").Val(noneMatched ? "NoMatch" : "Ambiguous")
                    .Key("message").Val(noneMatched
                        ? "清单里没有任何应用匹配“" + target + "”。请用 desktop_list_apps 查看准确的名字。"
                        : "匹配到多个应用，无法确定要启动哪一个，拒绝启动——宁可不动，也不要启错程序。");
                j.Key("candidates").Arr();
                foreach (Launcher.AppEntry a in cands)
                    j.Obj().Key("name").Val(a.Name).Key("exe").Val(a.Exe).End();
                j.EndArr().End();
                Console.WriteLine(j.ToString());
                return 3;
            }

            string err;
            int pid = Launcher.Launch(exe, out err);
            if (pid == 0)
            {
                Jw bad = new Jw();
                bad.Obj().Key("ok").Bool(false)
                    .Key("error").Val("LaunchFailed")
                    .Key("message").Val(err)
                    .Key("exe").Val(exe)
                 .End();
                Console.WriteLine(bad.ToString());
                return 4;
            }

            Jw ok = new Jw();
            ok.Obj().Key("ok").Bool(true)
                .Key("pid").Num(pid)
                .Key("exe").Val(exe)
             .End();
            Console.WriteLine(ok.ToString());
            return 0;
        }

        /// <summary>
        /// 急停闸门的状态查询与解除。用法：dsc.exe stop [--clear] [--open]
        ///
        ///   · 无参数   → **只查看状态，绝不写文件**
        ///   · --clear  → 解除闸门（恢复操作）
        ///   · --open   → 与 --clear 同义，名字更贴合"开启 computer use"的语境
        ///
        /// **拉闸是 halt 命令的职责**，不是 stop 的。这两者原来混在一起：
        /// 无参数时 stop 会写闸门文件，而帮助文本写的是"查看当前闸门状态"。
        /// 后果在第三方实测里出现过——插件的拒绝提示教用户
        /// "确认安全后用 dsc.exe stop --clear 解除"，而看到提示的人
        /// 最自然的动作是先跑一次 dsc.exe stop 看看现在是不是真被挡着，
        /// **于是又把闸门拉上了**。现场表现是"解除 → 立刻又被拉下"，
        /// 一度被误诊成 ESC 硬件抖动导致的死循环。
        ///
        /// 查询类命令带写副作用是很坏的形状：它让"查看"变成"操作"，
        /// 而且**不会有任何报错**，只会让人以为是别的东西在捣鬼。
        ///
        /// 为什么 --clear 必须**回读确认**：
        /// File.Delete 失败时**不抛异常**（只设 last-error），所以
        /// "删过了"不等于"删掉了"。原先的写法删完就无条件报 cleared=true，
        /// 结果是"报了已解除、实际还锁着"——用户看到 ok 却什么都做不了。
        /// 现在删完回读一次，删不掉就报 false 并把原因说出来。
        /// </summary>
        private static int CmdStop(string[] args)
        {
            bool wantClear = false;
            foreach (string a in args)
            {
                if (a == "--clear" || a == "--open") wantClear = true;
            }

            string gate = GatePath();
            bool cleared = false;
            string note;

            if (wantClear)
            {
                if (!File.Exists(gate))
                {
                    note = "闸门本来就不存在";
                }
                else
                {
                    try
                    {
                        File.Delete(gate);
                        // 回读：File.Delete 不抛异常也可能没删掉
                        cleared = !File.Exists(gate);
                        note = cleared
                            ? "闸门已解除"
                            : "闸门仍存在——删除被拒绝（检查文件是否只读或权限不足）";
                    }
                    catch (Exception e)
                    {
                        note = "解除失败：" + e.GetType().Name + ": " + e.Message;
                    }
                }
            }
            else
            {
                // ★ 只查看，不写任何文件。
                //
                // 这里原来会写闸门——见上面的类注释，那是个会让人误诊的副作用。
                // 现在给的是"当前状态 + 接下来该敲哪条命令"，因为查状态的人
                // 真正想知道的是"我被挡着吗、怎么解"。
                bool halted = IsHalted();
                note = halted
                    ? "闸门处于拉下状态——后续所有变更性操作都会被拒绝。" +
                      "确认安全后执行：dsc.exe stop --clear"
                    : "闸门未触发，操作正常。要主动拉下（急停）用：dsc.exe halt";
            }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("halted").Bool(IsHalted())
                .Key("mode").Val(wantClear ? "clear" : "query");
            // cleared 只在解除模式下出现。
            //
            // 查询模式**不输出这个键**——第一版输出的是字符串 "null"，
            // 那在 JSON 里是字符串而不是 null 值，调用方会把
            // "这次没解除"误读成"解除失败"。少一个键，好过一个语义可疑的键。
            if (wantClear) j.Key("cleared").Bool(cleared);
            j.Key("note").Val(note)
             .Key("gatePath").Val(gate)
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 检测窗口有哪些部分被别的窗口盖住了。
        ///
        /// **为什么必须有这个**：截图走的是 `CopyFromScreen`，它抓的是
        /// **屏幕上的那块区域**，不是窗口自身的内容。窗口被遮挡时，
        /// 遮挡者的画面会原样被截进来 —— 而不报任何错。
        ///
        /// 第三方实测踩到的正是这个：截一个 QQ音乐 窗口（窗口列表明确写的
        /// exe=QQMusic.exe），拿到的图却是 DSH 自己的界面，因为 QQ音乐
        /// 那时被 DSH 盖着。差一点据此得出"QQ音乐的界面内容是 DSH"的
        /// 错误结论。**静默返回别人的画面，比直接报错危险得多**——
        /// 调用方会基于错误画面做决策，而且完全无从察觉。
        ///
        /// 做法：在矩形内采五个点（中心 + 四角内缩），逐点问
        /// `WindowFromPoint` 那个点实际属于哪个窗口（用 GA_ROOT 归到顶层，
        /// 免得把自己窗口的子控件误判成遮挡）。任何一个点属于别人就算被遮挡。
        ///
        /// 采样而不是逐像素，是因为逐像素要几百万次跨进程调用；
        /// 五个点足以覆盖"盖住一大块"这种实际情况，而细缝遮挡对截图
        /// 的可用性影响很小。
        /// </summary>
        /// <summary>
        /// w 是不是 owner 的弹出窗口（下拉菜单、提示、右键菜单）。
        ///
        /// 沿 GW_OWNER 往上走几层，看能不能遇到 owner。限制层数是为了防止
        /// 异常数据造成死循环——owner 链正常情况下很短。
        /// </summary>
        private static bool IsPopupOf(IntPtr w, IntPtr owner)
        {
            IntPtr h = w;
            for (int i = 0; i < 8 && h != IntPtr.Zero; i++)
            {
                IntPtr o = Native.GetWindow(h, Native.GW_OWNER);
                if (o == IntPtr.Zero) return false;
                if (o == owner) return true;
                h = o;
            }
            return false;
        }

        private static List<string> DetectOcclusion(IntPtr hwnd, Native.RECT r)
        {
            List<string> blocked = new List<string>();
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return blocked;

            // 目标窗口所属进程（用于报告，**不再用它判断遮挡**）。
            uint selfPid = 0;
            Native.GetWindowThreadProcessId(hwnd, out selfPid);

            // 内缩：窗口边缘常有阴影、圆角或 1px 边框，
            // 贴着边界采样容易把阴影判成遮挡。
            int inset = Math.Max(8, Math.Min(w, h) / 33);
            int midX = r.Left + w / 2, midY = r.Top + h / 2;

            string[] labels = { "中心", "左上", "右上", "左下", "右下" };
            int[] xs = { midX, r.Left + inset, r.Right - inset, r.Left + inset, r.Right - inset };
            int[] ys = { midY, r.Top + inset, r.Top + inset, r.Bottom - inset, r.Bottom - inset };

            for (int i = 0; i < labels.Length; i++)
            {
                Native.POINT p = new Native.POINT();
                p.X = xs[i];
                p.Y = ys[i];

                IntPtr hit = Native.WindowFromPoint(p);
                if (hit == IntPtr.Zero) continue;
                IntPtr root = Native.GetAncestor(hit, Native.GA_ROOT);
                if (root == hwnd) continue;          // 属于目标窗口（含其子控件）

                uint pid;
                Native.GetWindowThreadProcessId(root, out pid);

                // ── 用 owner 关系判断，而不是"同一个进程" ──
                //
                // 最初用的是"同 pid 就跳过"，为了修掉"下拉菜单被当成遮挡"。
                // 但一个进程可以有**多个独立主窗口**：Word 开两个文档、
                // 浏览器多个窗口。那种情况下，另一个主窗口盖住目标时，
                // 按 pid 判断就会漏报真正的遮挡。
                //
                // owner 链才是准确判据：弹窗的 GW_OWNER 指向宿主窗口。
                //   · root == hwnd                → 目标自己
                //   · root 的 owner 链含 hwnd     → 目标的弹窗
                //   · 否则                        → 真遮挡
                if (IsPopupOf(root, hwnd)) continue;

                string exe = BaseName(ProcessPath(pid));
                blocked.Add(labels[i] + "=" + (exe.Length > 0 ? exe : "hwnd:" + root.ToInt64()));
            }
            return blocked;
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
            IntPtr hwnd = ParseHwnd(args, "screenshot");
            // 急停检查第一：截图就是界面内容本身，同样受闸门约束。
            RequireNotHalted("screenshot");
            // 白名单校验。
            RequireAllowedWindow(hwnd, "screenshot");

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

            // ── 保存前先确认目录可写 ──
            //
            // 实测踩到的：dsc.exe 是沙箱子进程，**只能写会话工作区**。
            // 默认保存路径用 %TEMP%，那在工作区外，于是写不进去。
            // 而 Bitmap.Save 对"路径不可写"报的是
            // **"GDI+ 中发生一般性错误"** —— 完全指错方向，
            // 从这条消息看不出真正原因是权限。
            //
            // 所以先实际写一个探测文件。不可写就明确说是权限问题、
            // 以及该怎么办（显式给一个工作区内的路径）。
            if (!CanWriteNear(outPath))
            {
                Jw bad = new Jw();
                bad.Obj().Key("ok").Bool(false)
                    .Key("error").Val("OutputNotWritable")
                    .Key("path").Val(outPath)
                    .Key("message").Val(
                        "保存位置不可写：" + outPath + "\n" +
                        "dsc.exe 是沙箱子进程，只能写会话工作区；默认保存位置用的是系统临时目录，" +
                        "那通常在工作区之外。\n" +
                        "请显式给出一个工作区内的路径，例如：dsc.exe screenshot " +
                        hwnd.ToInt64() + " \"<工作区>\\shot.png\"")
                 .End();
                Console.WriteLine(bad.ToString());
                return 7;
            }

            try
            {
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    bmp.Save(outPath, ImageFormat.Png);
                }
            }
            catch (Exception e)
            {
                // 兜底：把真实原因说出来，不要让调用方只看到"GDI+ 一般性错误"。
                Jw bad = new Jw();
                bad.Obj().Key("ok").Bool(false)
                    .Key("error").Val("CaptureFailed")
                    .Key("path").Val(outPath)
                    .Key("stage").Val("save")
                    .Key("exception").Val(e.GetType().Name)
                    .Key("message").Val(e.GetType().Name + ": " + e.Message)
                 .End();
                Console.WriteLine(bad.ToString());
                return 8;
            }

            // 再回读确认文件真的落盘了——"Save 没抛异常"不等于"文件存在"。
            if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            {
                Jw bad = new Jw();
                bad.Obj().Key("ok").Bool(false)
                    .Key("error").Val("OutputMissing")
                    .Key("path").Val(outPath)
                    .Key("message").Val("保存调用没有抛异常，但文件不存在或是空的。")
                 .End();
                Console.WriteLine(bad.ToString());
                return 9;
            }

            // 检测遮挡：截图抓的是屏幕区域，被盖住的地方拿到的是遮挡者的画面。
            // 必须在返回里说清楚，否则调用方会基于错误画面做决策且无从察觉。
            List<string> occluded = DetectOcclusion(hwnd, r);

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("filepath").Val(outPath)
                .Key("width").Num(w).Key("height").Num(h)
                .Key("originX").Num(r.Left).Key("originY").Num(r.Top)
                .Key("coords").Val("physical-pixels")
                .Key("occluded").Bool(occluded.Count > 0)
                .Key("occludedPoints").Val(string.Join(", ", occluded.ToArray()))
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

            // 解析：click <x> <y> [left|right] [--hwnd <窗口句柄>]
            //
            // --hwnd 是后加的，而且补的是一个**会点到别人身上**的缺口：
            // 原先原生程序拿不到目标窗口，只能按"落点当前属于谁"去校验和激活。
            // 于是目标 A 被已授权的 B 盖住时，它会激活并点击 B —— 调用方以为
            // 点的是 A。这种错不会报错，只会让操作落到错误的窗口上。
            IntPtr target = IntPtr.Zero;
            List<string> pos = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--hwnd")
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--hwnd 后面需要窗口句柄");
                    target = new IntPtr(long.Parse(args[++i]));
                }
                else pos.Add(args[i]);
            }
            if (pos.Count < 2) throw new ArgumentException("click 需要 x y 参数（桌面绝对物理像素）");
            int x = int.Parse(pos[0]);
            int y = int.Parse(pos[1]);

            // 第三个位置参数是按钮名；--count 是后加的（从 pos 里剥出来）。
            string clickButton = "left";
            int clickCount = 1;
            List<string> rest2 = new List<string>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (i == 2 && (pos[i] == "left" || pos[i] == "right" || pos[i] == "middle"))
                    clickButton = pos[i];
                else if (pos[i] == "--count")
                {
                    if (i + 1 >= pos.Count) throw new ArgumentException("--count 后面需要数字");
                    clickCount = int.Parse(pos[++i]);
                    if (clickCount < 1) clickCount = 1;
                    if (clickCount > 3) clickCount = 3;
                }
                else if (i >= 2) rest2.Add(pos[i]);
            }
            bool right = clickButton == "right";

            // 有目标窗口时先激活它。激活不只是为了注入能被接受，
            // 也是为了**把目标提到前台，让"落点属于谁"这件事变得可判断**。
            if (target != IntPtr.Zero)
            {
                RequireAllowedWindow(target, "click");
                RequireActivated(target, "click");
            }

            IntPtr hit = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
            IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, Native.GA_ROOT);
            uint hitPid = 0;
            if (root != IntPtr.Zero) Native.GetWindowThreadProcessId(root, out hitPid);
            string hitExe = BaseName(ProcessPath(hitPid));

            Console.Error.WriteLine("[click 诊断] 目标=(" + x + "," + y + ")" +
                " 指定窗口=" + target +
                " WindowFromPoint=" + hit + " root=" + root + " pid=" + hitPid +
                " exe=\"" + hitExe + "\"" +
                " 前台=" + Native.GetForegroundWindow() +
                " 白名单=[" + string.Join(",", Allowlist()) + "]");

            if (hit == IntPtr.Zero)
                throw new InvalidOperationException("坐标 (" + x + "," + y + ") 处没有窗口，拒绝点击。");

            if (target != IntPtr.Zero && root != target)
            {
                // 激活之后落点仍不属于目标 → 有东西盖着它。
                //
                // **这里必须拒绝，不能退回去点遮挡者**。这正是"点偏"的一种来源：
                // 调用方给的是 A 的坐标，结果点了 B，而报告里写着成功。
                throw new InvalidOperationException(
                    "拒绝点击：坐标 (" + x + "," + y + ") 现在属于窗口 " + root.ToInt64() +
                    "（" + (hitExe ?? "?") + "），不是指定的目标窗口 " + target.ToInt64() + "。\n" +
                    "目标窗口可能被置顶窗口盖住，或坐标已经不在它身上。\n" +
                    "先移开遮挡、或重新截图取坐标——不会替你去点那个盖住它的窗口。");
            }

            RequireAllowedWindow(root, "click");
            // 没有指定目标时，注入前必须激活落点窗口（非前台状态下注入会被系统压制，实测）。
            if (target == IntPtr.Zero) RequireActivated(root, "click");

            MoveCursorAbsolute(x, y);
            SendMouseButton(clickButton, clickCount);

            // 回读光标位置：调用成功不代表点在了期望处，读回来才是证据。
            Native.POINT after;
            bool gotCursor = Native.GetCursorPos(out after);
            string actual = gotCursor ? (after.X + "," + after.Y) : "?";
            // 容差要和 MoveCursorAbsolute 的校验收一致（±2）。
            //
            // 两处用不同判据的后果实测到了：SetCursorPos 落到 187 而目标是 186，
            // MoveCursorAbsolute 认为没问题（差 1 < 2），而这里用精确相等判成
            // "没有真正发生" —— 于是一次成功的点击被报成失败。
            // 同一个概念在两处用不同标准，结果就是自相矛盾。
            bool landed = gotCursor &&
                          Math.Abs(after.X - x) <= 2 && Math.Abs(after.Y - y) <= 2;

            // 点完之后再问一次"这个点现在属于谁"。
            //
            // 这一条是"动作后的重新观察"里最便宜、也最有用的一条：
            //   · 光标有没有真的移过去（landed）
            //   · 那个位置属于哪个应用（atExe）
            // 两者合起来能区分"根本没点成"和"点了但应用没反应"——
            // 在那之前，这两种情况返回的是一模一样的一句话。
            IntPtr hitAfter = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
            IntPtr rootAfter = hitAfter == IntPtr.Zero
                ? IntPtr.Zero : Native.GetAncestor(hitAfter, Native.GA_ROOT);
            uint pidAfter = 0;
            if (rootAfter != IntPtr.Zero) Native.GetWindowThreadProcessId(rootAfter, out pidAfter);
            string atExe = BaseName(ProcessPath(pidAfter));

            // ── 点完之后，问一句"这一点下面是哪个元素" ──
            //
            // 这不是锦上添花：实测我自己会用目测估坐标而不读树，
            // 于是把搜索框估低 30 像素点到了"下载客户端"、把标签页当成标题栏。
            // 插件一直给了精确矩形，问题是"调用方要记得先读树"这种约束
            // 和"助手要记得开指示层"是同一种病。
            //
            // 所以把检查塞进工具本身：每次坐标点击都顺带报告这一点是什么。
            // FromPoint 失败时不阻断点击（降级为不报告），因为坐标点击
            // 在某些环境仍然是有意义的最后手段。
            string atElement = null;
            string atElementError = null;
            try { bool _a; atElement = DescribePoint(x, y, out _a); }
            catch (Exception e2) { atElementError = e2.Message; }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("clickedX").Num(x).Key("clickedY").Num(y)
                .Key("cursorAfter").Val(actual)
                .Key("cursorLanded").Bool(landed)
                .Key("targetHwnd").Num(target.ToInt64())
                .Key("atExe").Val(atExe)
                .Key("atHwnd").Num(rootAfter.ToInt64())
                .Key("button").Val(clickButton).Key("count").Num(clickCount);
            if (atElement != null) j.Key("atElement").Raw(atElement);
            if (atElementError != null) j.Key("atElementError").Val(atElementError);
            j.End();
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

        /// <summary>
        /// 把鼠标移动到指定点，**不点击**。用法：move &lt;x&gt; &lt;y&gt; [--hwnd H]
        ///
        /// 用途是触发 hover（悬停菜单、工具提示）。之前只有 click，
        /// 而 click 会按下再抬起，很多界面因此走的是"点击"分支而不是"悬停"分支。
        /// </summary>
        private static int CmdMove(string[] args)
        {
            RequireNotHalted("move");
            List<string> pos = new List<string>();
            IntPtr target = ParseOptionalHwnd(args, pos);
            if (pos.Count < 2) throw new ArgumentException("move 需要 x y 参数（桌面绝对物理像素）");
            int x = int.Parse(pos[0]);
            int y = int.Parse(pos[1]);

            if (target != IntPtr.Zero)
            {
                RequireAllowedWindow(target, "move");
                RequireActivated(target, "move");
            }

            IntPtr hit = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
            IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, Native.GA_ROOT);
            if (hit == IntPtr.Zero)
                throw new InvalidOperationException("坐标 (" + x + "," + y + ") 处没有窗口，拒绝移动。");
            if (target != IntPtr.Zero && root != target)
                throw new InvalidOperationException(
                    "拒绝移动：坐标 (" + x + "," + y + ") 属于窗口 " + root.ToInt64() +
                    "，不是指定的目标窗口 " + target.ToInt64() + "。");
            RequireAllowedWindow(root, "move");

            MoveCursorAbsolute(x, y);

            Native.POINT after;
            bool got = Native.GetCursorPos(out after);
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("x").Num(x).Key("y").Num(y)
                .Key("cursorAfter").Val(got ? (after.X + "," + after.Y) : "?")
                .Key("atExe").Val(BaseName(ProcessPath(PidOfWindow(root))))
                .Key("verified").Bool(false)
                .Key("note").Val("光标已移动，未点击。hover 效果是否出现要重新观察。")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 拖动：在起点按下，分步移动到终点，抬起。
        /// 用法：drag &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; [--hwnd H] [--steps N] [left|right|middle]
        ///
        /// ── 为什么必须分步移动 ──
        ///
        /// 直接把光标从 A 移到 B 再抬起，多数应用收不到中间的移动事件，
        /// 于是"拖拽"被当成一次普通点击 —— 文件不会被拖走，滚动条不会动。
        /// 真实拖动要靠一连串中间位置，让应用看到按下状态下的位移。
        ///
        /// ── 实测记录（换实现之前请先读这段）──
        ///
        /// 1. 用 SetCursorPos 做中间移动**不行**。它只改光标位置，不产生
        ///    鼠标移动消息，应用收不到 WM_MOUSEMOVE。实测：拖记事本标题栏，
        ///    20 步走完、光标停在终点、按键也正确释放，**窗口位置一点没变**。
        ///    把步进从 12ms 放到 30ms 也没用 —— 不是时序问题。
        ///    必须用 mouse_event(MOUSEEVENTF_MOVE) 才会生成移动事件。
        ///
        /// 2. 同样的实现，在**文档里拖选文字**是生效的：状态栏从
        ///    "6,755 个字符" 变成 "70 个字符，共"，选中内容也高亮了。
        ///    所以拖动本身是通的。
        ///
        /// 3. 拖记事本的标题栏**曾经**不动，而我当时把原因猜成"UWP 标题栏
        ///    不吃合成消息"——**那个结论是错的**，已用 desktop_whatis 纠正。
        ///
        ///    真实原因：我以为的标题栏位置 (1530,197) 上面盖着一层
        ///    `Pane @(1169,191 518x48)`（标签栏容器）。鼠标事件被那个 Pane
        ///    收走，它不处理窗口拖动。真正的标题栏在 (1714,205)——
        ///    那里在 Pane 右边、窗口按钮左边。
        ///
        ///    从 (1714,205) 拖到 (1550,327)，窗口从 (1100,177) 移到了
        ///    (934,300)。**所以 UWP 标题栏是吃 mouse_event 的。**
        ///
        ///    教训：控件矩形会重叠，而 WindowFromPoint / FromPoint 返回的是
        ///    z 序最上层的那个。想知道"这一点到底是什么"，用 desktop_whatis 问，
        ///    不要看截图猜。
        ///
        /// ── 为什么每一步都要检查急停 ──
        ///
        /// 拖动是有副作用的动作（移动文件、调整窗口大小），而且耗时最长。
        /// 用户在这期间按 ESC 必须立即停下 —— 但**停下的同时必须松开按键**，
        /// 否则鼠标会一直处于按下状态，用户接下来在桌面上的任何点击都会变成拖拽。
        /// 那是一种很难自己恢复的坏状态，所以用 try/finally 保证释放。
        /// </summary>
        private static int CmdDrag(string[] args)
        {
            RequireNotHalted("drag");

            string button = "left";
            int steps = 12;
            List<string> pos = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--steps")
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("--steps 后面需要数字");
                    steps = int.Parse(args[++i]);
                    if (steps < 2) steps = 2;
                    if (steps > 200) steps = 200;
                }
                else if (args[i] == "left" || args[i] == "right" || args[i] == "middle")
                    button = args[i];
                else pos.Add(args[i]);
            }
            // --hwnd 要从 pos 里剥掉
            IntPtr target = IntPtr.Zero;
            List<string> clean = new List<string>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (pos[i] == "--hwnd")
                {
                    if (i + 1 >= pos.Count) throw new ArgumentException("--hwnd 后面需要窗口句柄");
                    target = new IntPtr(long.Parse(pos[++i]));
                }
                else clean.Add(pos[i]);
            }
            pos = clean;

            if (pos.Count < 4)
                throw new ArgumentException("drag 需要 x1 y1 x2 y2（桌面绝对物理像素）");
            int x1 = int.Parse(pos[0]), y1 = int.Parse(pos[1]);
            int x2 = int.Parse(pos[2]), y2 = int.Parse(pos[3]);

            if (target != IntPtr.Zero)
            {
                RequireAllowedWindow(target, "drag");
                RequireActivated(target, "drag");
            }
            else
            {
                IntPtr h = Native.WindowFromPoint(new Native.POINT { X = x1, Y = y1 });
                IntPtr rt = h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
                if (rt != IntPtr.Zero) RequireAllowedWindow(rt, "drag");
            }

            uint down, up;
            ButtonFlags(button, out down, out up);

            bool pressed = false;
            try
            {
                MoveCursorAbsolute(x1, y1);
                System.Threading.Thread.Sleep(80);
                Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                pressed = true;
                // 按下之后要**等一会儿再动**。
                //
                // 系统判定"这是拖动"而不是"这是点击"需要看到按下状态下
                // 的位移，而且判定本身要时间。实测 60ms 太短 —— 20 步 ×12ms
                // 的拖动在记事本窗口上完全没有生效（窗口位置一点没变），
                // 表现是"拖了但什么也没发生"。
                System.Threading.Thread.Sleep(150);

                for (int i = 1; i <= steps; i++)
                {
                    if (IsHalted())
                        throw new InvalidOperationException(
                            "拖动被急停中断（已移动 " + (i - 1) + " / " + steps + " 步）。按键已释放，不会卡在按下状态。");

                    int tx = x1 + (x2 - x1) * i / steps;
                    int ty = y1 + (y2 - y1) * i / steps;

                    // ★ 必须用 mouse_event(MOVE)，**不能用 SetCursorPos**。
                    //
                    // SetCursorPos 只改光标位置，**不产生鼠标移动消息**。
                    // 应用收不到 WM_MOUSEMOVE，于是完全不认为在拖动 ——
                    // 实测：拖记事本标题栏，20 步走完、光标停在终点、
                    // 按键也正确释放，而窗口位置一点没变。
                    // 这不是时序问题（把步进从 12ms 放到 30ms 也没用）。
                    //
                    // mouse_event(MOUSEEVENTF_MOVE) 才会让系统生成移动事件，
                    // 窗口的拖动逻辑才看得到"按下状态下的位移"。
                    Native.POINT cur;
                    if (!Native.GetCursorPos(out cur)) { cur.X = tx; cur.Y = ty; }
                    int mdx = tx - cur.X;
                    int mdy = ty - cur.Y;
                    if (mdx == 0 && mdy == 0)
                    {
                        System.Threading.Thread.Sleep(20);
                        continue;
                    }

                    // 传**相对**位移。鼠标加速会改变实际落点，所以每一轮都用
                    // "目标 - 当前实际位置"重算 —— 这样即使被加速放大或缩小，
                    // 循环会自己收敛到终点，而不是累积偏差。
                    // 签名里 dx/dy 是 int（有符号）—— 相对移动传负数表示向左/上，
                    // 不需要位转换。第一版写成 unchecked((uint)...) 编译不过，
                    // 反而提醒了这一点。
                    Native.mouse_event(Native.MOUSEEVENTF_MOVE, mdx, mdy, 0, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(30);
                }

                // 抬起前强制对齐终点 —— 理由同 dragpath：
                // mouse_event 受鼠标加速影响，落点会偏；而落点决定 drop 位置。
                Native.SetCursorPos(x2, y2);
                System.Threading.Thread.Sleep(60);
                System.Threading.Thread.Sleep(120);
                Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                pressed = false;
            }
            finally
            {
                // ★ 无论怎么退出，**必须**抬起来。
                //   上面那条 throw 会跳过正常路径，如果没有这个 finally，
                //   用户就得到一个"一直按着左键"的鼠标。
                if (pressed)
                {
                    try { Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero); }
                    catch { }
                }
            }

            Native.POINT after;
            bool got = Native.GetCursorPos(out after);
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("from").Val(x1 + "," + y1)
                .Key("to").Val(x2 + "," + y2)
                .Key("steps").Num(steps)
                .Key("button").Val(button)
                .Key("cursorAfter").Val(got ? (after.X + "," + after.Y) : "?")
                .Key("verified").Bool(false)
                .Key("note").Val(
                    "拖动事件已发出，按键已确认释放。拖动的结果（文件是否移动、滚动条是否变化）" +
                    "要重新观察才能确认。")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 发送鼠标滚轮。用法：wheel &lt;格数&gt; [--at x,y] [--hwnd H] [--horiz]
        ///
        /// 格数是**带符号的整数**：正数向上/远离用户，负数向下。内部乘 WHEEL_DELTA(120)。
        ///
        /// ── 为什么要有它 ──
        ///
        /// 之前只能靠 UIA 的 ScrollPattern（do_action 的 Scroll Up/Down）。
        /// 那条路只对"声明了滚动模式的控件"有效，网页、画布、自绘列表全都不认。
        /// 真实滚轮是通用手段：滚到哪由系统送给光标下的窗口。
        ///
        /// ── 为什么要指定位置 ──
        ///
        /// 滚轮的作用对象是**光标下面的窗口**，不是"当前前台窗口"。
        /// 所以不指定位置就可能滚错窗口 —— 光标停在哪就滚哪。
        /// 给了 --at 就先移过去，同时校验落点属于目标窗口。
        /// </summary>
        private static int CmdWheel(string[] args)
        {
            RequireNotHalted("wheel");

            bool horiz = false;
            IntPtr target = IntPtr.Zero;
            int atX = int.MinValue, atY = int.MinValue;
            List<string> pos = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--horiz") { horiz = true; continue; }
                if (args[i] == "--hwnd")
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("--hwnd 后面需要窗口句柄");
                    target = new IntPtr(long.Parse(args[++i]));
                    continue;
                }
                if (args[i] == "--at")
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("--at 后面需要 x,y");
                    string[] xy = args[++i].Split(',');
                    if (xy.Length != 2) throw new ArgumentException("--at 的格式是 x,y");
                    atX = int.Parse(xy[0]); atY = int.Parse(xy[1]);
                    continue;
                }
                pos.Add(args[i]);
            }
            if (pos.Count < 1) throw new ArgumentException("wheel 需要格数（正=上，负=下）");
            int notches = int.Parse(pos[0]);
            if (notches == 0) throw new ArgumentException("格数不能是 0");

            if (atX != int.MinValue)
            {
                IntPtr hit = Native.WindowFromPoint(new Native.POINT { X = atX, Y = atY });
                IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, Native.GA_ROOT);
                if (hit == IntPtr.Zero)
                    throw new InvalidOperationException("--at (" + atX + "," + atY + ") 处没有窗口。");
                if (target != IntPtr.Zero && root != target)
                    throw new InvalidOperationException(
                        "拒绝滚动：--at 处属于窗口 " + root.ToInt64() +
                        "，不是指定的目标窗口 " + target.ToInt64() + "。");
                if (target != IntPtr.Zero) RequireAllowedWindow(target, "wheel");
                else RequireAllowedWindow(root, "wheel");
                MoveCursorAbsolute(atX, atY);
                System.Threading.Thread.Sleep(40);
            }
            else if (target != IntPtr.Zero)
            {
                RequireAllowedWindow(target, "wheel");
                // 没给位置时把光标放到目标窗口中心，否则会滚到光标当前所在窗口
                RequireActivated(target, "wheel");
                Native.RECT r;
                if (Native.GetWindowRect(target, out r))
                {
                    MoveCursorAbsolute((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
                    System.Threading.Thread.Sleep(40);
                }
            }

            uint flag = horiz ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL;
            Native.mouse_event(flag, 0, 0, (uint)(notches * Native.WHEEL_DELTA), UIntPtr.Zero);

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("notches").Num(notches)
                .Key("delta").Num(notches * Native.WHEEL_DELTA)
                .Key("horizontal").Bool(horiz)
                .Key("at").Val(atX != int.MinValue ? (atX + "," + atY) : "（窗口中心）")
                .Key("verified").Bool(false)
                .Key("note").Val("滚轮事件已发出。滚动结果要重新观察才能确认。")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>从参数里取 --hwnd，其余按位置参数返回。</summary>
        private static IntPtr ParseOptionalHwnd(string[] args, List<string> positional)
        {
            IntPtr target = IntPtr.Zero;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--hwnd")
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("--hwnd 后面需要窗口句柄");
                    target = new IntPtr(long.Parse(args[++i]));
                }
                else positional.Add(args[i]);
            }
            return target;
        }


        /// <summary>
        /// 按住鼠标键不放。用法：hold &lt;x&gt; &lt;y&gt; [left|right|middle] [--hwnd H]
        ///
        /// ── 用完必须 release ──
        ///
        /// 这是短进程：命令返回后进程就退出了，而**按键状态留在系统里**。
        /// 忘了松开的话，用户接下来任何点击都会变成拖拽 —— 一种很难自己
        /// 恢复的状态。所以返回里会明确提示，并且 release 命令不需要坐标、
        /// 不需要白名单、也不受急停限制（急停时更需要松开）。
        /// </summary>
        private static int CmdHold(string[] args)
        {
            RequireNotHalted("hold");

            string button = "left";
            List<string> pos = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "left" || args[i] == "right" || args[i] == "middle") button = args[i];
                else pos.Add(args[i]);
            }
            IntPtr target = IntPtr.Zero;
            List<string> clean = new List<string>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (pos[i] == "--hwnd")
                {
                    if (i + 1 >= pos.Count) throw new ArgumentException("--hwnd 后面需要窗口句柄");
                    target = new IntPtr(long.Parse(pos[++i]));
                }
                else clean.Add(pos[i]);
            }
            pos = clean;
            if (pos.Count < 2) throw new ArgumentException("hold 需要 x y");

            int x = int.Parse(pos[0]), y = int.Parse(pos[1]);

            if (target != IntPtr.Zero)
            {
                RequireAllowedWindow(target, "hold");
                RequireActivated(target, "hold");
            }

            IntPtr hit = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
            IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, Native.GA_ROOT);
            if (hit == IntPtr.Zero)
                throw new InvalidOperationException("坐标 (" + x + "," + y + ") 处没有窗口。");
            if (target != IntPtr.Zero && root != target)
                throw new InvalidOperationException(
                    "拒绝按住：坐标处属于窗口 " + root.ToInt64() + "，不是目标窗口 " + target.ToInt64() + "。");
            RequireAllowedWindow(root, "hold");

            uint down, up;
            ButtonFlags(button, out down, out up);
            MoveCursorAbsolute(x, y);
            System.Threading.Thread.Sleep(60);
            Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("held").Val(button)
                .Key("x").Num(x).Key("y").Num(y)
                .Key("atExe").Val(BaseName(ProcessPath(PidOfWindow(root))))
                .Key("verified").Bool(false)
                .Key("warning").Val(
                    "按键现在处于**按下状态**，并且会一直保持到你调用 release。" +
                    "不松开的话，用户接下来任何点击都会变成拖拽。用完请立即执行：dsc.exe release")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 松开鼠标键。用法：release [left|right|middle] [--all]
        ///
        /// 刻意不做三件事：不需要坐标、不检查白名单、**不受急停限制**。
        /// 理由：松开按键是恢复动作。急停时用户最需要的正是"把按着的东西松开"，
        /// 这时候拦下来只会让鼠标卡得更久。
        /// </summary>
        private static int CmdRelease(string[] args)
        {
            bool all = false;
            string button = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--all") all = true;
                else if (args[i] == "left" || args[i] == "right" || args[i] == "middle") button = args[i];
            }

            List<string> released = new List<string>();
            string[] buttons = all || button == null
                ? new string[] { "left", "right", "middle" }
                : new string[] { button };

            foreach (string b in buttons)
            {
                uint down, up;
                ButtonFlags(b, out down, out up);
                Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                released.Add(b);
                System.Threading.Thread.Sleep(20);
            }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("released").Val(string.Join(",", released.ToArray()))
                .Key("verified").Bool(false)
                .Key("note").Val(
                    "已发送松开事件。注意这只能松开**本插件按下的键**，" +
                    "如果用户自己正按着鼠标，也会被一起松开——鼠标只有一个。")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 带路径的拖动：按下 → 依次经过每个拐点 → 抬起。用法：
        /// dragpath &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; [x3 y3 ...] [--hwnd H] [--steps N] [left|right|middle]
        ///
        /// 为什么需要它：直线拖拽表达不了"绕过去"。例如从行首拖到第 5 行，
        /// 直线会扫过中间的控件；而真实的操作是"先垂直往下、再水平移动"。
        /// 多点路径就是给这种情况用的。
        ///
        /// 与 drag 一样：每步检查急停、退出时一定释放按键。
        /// </summary>
        private static int CmdDragPath(string[] args)
        {
            RequireNotHalted("dragpath");

            string button = "left";
            int stepsPerLeg = 8;
            List<string> pos = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--steps")
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("--steps 后面需要数字");
                    stepsPerLeg = int.Parse(args[++i]);
                    if (stepsPerLeg < 2) stepsPerLeg = 2;
                    if (stepsPerLeg > 100) stepsPerLeg = 100;
                }
                else if (args[i] == "left" || args[i] == "right" || args[i] == "middle") button = args[i];
                else pos.Add(args[i]);
            }
            IntPtr target = IntPtr.Zero;
            List<string> clean = new List<string>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (pos[i] == "--hwnd")
                {
                    if (i + 1 >= pos.Count) throw new ArgumentException("--hwnd 后面需要窗口句柄");
                    target = new IntPtr(long.Parse(pos[++i]));
                }
                else clean.Add(pos[i]);
            }
            pos = clean;

            if (pos.Count < 4 || pos.Count % 2 != 0)
                throw new ArgumentException("dragpath 需要成对的坐标：x1 y1 x2 y2 [x3 y3 ...]");

            List<int> xs = new List<int>();
            List<int> ys = new List<int>();
            for (int i = 0; i + 1 < pos.Count; i += 2)
            {
                xs.Add(int.Parse(pos[i]));
                ys.Add(int.Parse(pos[i + 1]));
            }

            if (target != IntPtr.Zero)
            {
                RequireAllowedWindow(target, "dragpath");
                RequireActivated(target, "dragpath");
            }
            else
            {
                IntPtr h = Native.WindowFromPoint(new Native.POINT { X = xs[0], Y = ys[0] });
                IntPtr rt = h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
                if (rt != IntPtr.Zero) RequireAllowedWindow(rt, "dragpath");
            }

            uint down2, up2;
            ButtonFlags(button, out down2, out up2);

            bool pressed = false;
            int moved = 0;
            try
            {
                MoveCursorAbsolute(xs[0], ys[0]);
                System.Threading.Thread.Sleep(80);
                Native.mouse_event(down2, 0, 0, 0, UIntPtr.Zero);
                pressed = true;
                System.Threading.Thread.Sleep(150);

                for (int leg = 1; leg < xs.Count; leg++)
                {
                    int ax = xs[leg - 1], ay = ys[leg - 1];
                    int bx = xs[leg], by = ys[leg];
                    for (int i = 1; i <= stepsPerLeg; i++)
                    {
                        if (IsHalted())
                            throw new InvalidOperationException(
                                "拖动被急停中断（第 " + leg + " 段，已移动 " + moved + " 步）。按键已释放。");

                        int tx = ax + (bx - ax) * i / stepsPerLeg;
                        int ty = ay + (by - ay) * i / stepsPerLeg;
                        Native.POINT cur;
                        if (!Native.GetCursorPos(out cur)) { cur.X = tx; cur.Y = ty; }
                        int mdx = tx - cur.X, mdy = ty - cur.Y;
                        if (mdx != 0 || mdy != 0)
                        {
                            Native.mouse_event(Native.MOUSEEVENTF_MOVE, mdx, mdy, 0, UIntPtr.Zero);
                            moved++;
                        }
                        System.Threading.Thread.Sleep(30);
                    }
                }

                // ★ 抬起之前**强制对齐到终点**。
                //
                // 中间步用 mouse_event(MOVE) 是为了产生移动事件，但它受鼠标
                // 加速影响，闭环校正只能减小误差、不能消除。实测拖到 (250,500)
                // 时回读是 (227,573) —— 差了 73 像素。
                //
                // 而**落点决定东西放在哪里**：文件拖到错误位置、滚动条拖不到位。
                // 所以最后一步换成 SetCursorPos：它不产生移动事件，但能精确
                // 摆放光标；抬起时系统用的是光标当前所在位置。
                Native.SetCursorPos(xs[xs.Count - 1], ys[ys.Count - 1]);
                System.Threading.Thread.Sleep(60);
                System.Threading.Thread.Sleep(120);
                Native.mouse_event(up2, 0, 0, 0, UIntPtr.Zero);
                pressed = false;
            }
            finally
            {
                if (pressed)
                {
                    try { Native.mouse_event(up2, 0, 0, 0, UIntPtr.Zero); } catch { }
                }
            }

            StringBuilder pathSb = new StringBuilder();
            for (int i = 0; i < xs.Count; i++)
            {
                if (i > 0) pathSb.Append(" -> ");
                pathSb.Append('(').Append(xs[i]).Append(',').Append(ys[i]).Append(')');
            }

            Native.POINT final;
            bool got = Native.GetCursorPos(out final);
            Jw jj = new Jw();
            jj.Obj().Key("ok").Bool(true)
                .Key("path").Val(pathSb.ToString())
                .Key("legs").Num(xs.Count - 1)
                .Key("stepsPerLeg").Num(stepsPerLeg)
                .Key("movedSteps").Num(moved)
                .Key("button").Val(button)
                .Key("cursorAfter").Val(got ? (final.X + "," + final.Y) : "?")
                .Key("verified").Bool(false)
                .Key("note").Val("路径拖动已发出，按键已释放。结果要重新观察。")
             .End();
            Console.WriteLine(jj.ToString());
            return 0;
        }

        private static uint PidOfWindow(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        private static int CmdClickElement(string[] args)
        {
            RequireNotHalted("click_element");
            if (args.Length < 3) throw new ArgumentException("click_element 需要 hwnd 与描述符");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            RequireAllowedWindow(hwnd, "click_element");
            string desc = args[2];
            bool right = args.Length > 3 && args[3] == "right";

            // 注入前必须激活：非前台状态下注入会被系统压制（实测）。
            RequireActivated(hwnd, "click_element");

            Dictionary<string, string> want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in desc.Split('/'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) want[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            if (want.Count == 0) throw new ArgumentException("描述符为空，例如 role=Button/name=保存");

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            MatchTally tally = new MatchTally();
            AutomationElement found = FindByDescriptor(root, want, 0, new SearchBudget { Remaining = 4000, DeadlineMs = 5000 }, tally);
            if (tally.Count > 1)
                throw new InvalidOperationException(
                    "描述符匹配到多个元素（至少 " + tally.Count + " 个）：" + desc + "\n" +
                    "拒绝执行——点错其中哪一个都不会报错，而且可能做错事。" +
                    "请加更具体的条件（例如补上 aid=，或写更完整的 name）。");
            if (found == null)
                throw new InvalidOperationException("未找到匹配元素：" + desc);

            // ── 优先用 InvokePattern，而不是算矩形中心去点 ──
            //
            // InvokePattern 是控件自己声明的"我被触发了"的语义入口：
            //   · 不依赖坐标 → 不受 DPI / 缩放 / 遮挡影响
            //   · 不移动鼠标 → 不打断用户，也不会点到别处
            //   · 可达性更好 → 中心被别的控件压住时仍然有效
            //
            // 但它不是万能：很多自绘控件（画布、视频控件、部分 Electron 组件）
            // 根本不实现它；右键也没有对应的"Invoke"语义。
            // 所以这里只作为**优先级更高的尝试**，失败则原样退回坐标点击，
            // 并把实际用了哪种方式写进返回值——调用方不该去猜。
            //
            // 强制走坐标：给 --coords（有些控件实现 Invoke 但行为与真实点击不同）。
            string method = "";
            string invokeError = null;
            bool forceCoords = false;
            foreach (string a2 in args)
                if (a2 == "--coords") forceCoords = true;

            if (!right && !forceCoords)
            {
                object ip = null;   // 必须先赋值：out 在 try 里，catch 后可能未赋值
                bool hasInvoke = false;
                try { hasInvoke = found.TryGetCurrentPattern(InvokePattern.Pattern, out ip); }
                catch { hasInvoke = false; }

                if (hasInvoke)
                {
                    try
                    {
                        ((InvokePattern)ip).Invoke();
                        method = "InvokePattern";
                    }
                    catch (Exception ex)
                    {
                        // ★ 绝不能在 Invoke 抛异常之后再去点一次。
                        //
                        // 两种情况的语义完全不同：
                        //   · 元素**不支持** Invoke   → 什么都还没发生，可以改走坐标
                        //   · Invoke **抛异常**        → 应用可能已经开始执行了，
                        //                                只是结果未知
                        // 后者再补一次坐标点击，就是**可能重复触发** ——
                        // 点两次"提交"、发两条消息、买两次单。
                        //
                        // 第三方审查指出这条时说得对：异常不能证明应用没开始执行。
                        // 所以这里只记录，交给下面按"结果未知"处理，不重试。
                        invokeError = ex.GetType().Name + ": " + ex.Message;
                        Console.Error.WriteLine("[click_element] InvokePattern 抛异常，不再改道重试: " + invokeError);
                    }
                }
            }

            if (invokeError != null)
            {
                // 结果未知：**不重试**，也不假装失败。让调用方去观察。
                Jw ju = new Jw();
                ju.Obj().Key("ok").Bool(false)
                    .Key("error").Val("UnknownOutcome")
                    .Key("matched").Val(desc)
                    .Key("message").Val(
                        "已调用 InvokePattern 但它抛了异常：" + invokeError + "\n" +
                        "应用可能已经开始执行，也可能没有——异常证明不了是哪一种。" +
                        "**不会再去点一次**，因为重复触发可能点两次提交/发两条消息。" +
                        "请重新读树或截图确认界面状态，再决定下一步。")
                 .End();
                Console.WriteLine(ju.ToString());
                return 6;
            }

            if (method == "InvokePattern")
            {
                // 没有取矩形、没有移动鼠标。诚实说明：Invoke 是异步投递，
                // 返回成功只代表"控件接受了触发请求"，不代表界面已经变了。
                Jw jv = new Jw();
                jv.Obj().Key("ok").Bool(true)
                    .Key("matched").Val(desc)
                    .Key("method").Val("InvokePattern")
                    .Key("cursorMoved").Bool(false)
                    .Key("verified").Bool(false)
                    .Key("note").Val(
                        "已通过控件的 InvokePattern 触发，没有移动鼠标。" +
                        "Invoke 是异步投递，返回成功不代表界面已经改变——" +
                        "要确认效果请重读一次界面或截图。")
                 .End();
                Console.WriteLine(jv.ToString());
                return 0;
            }

            Native.RECT r;
            GetElementRect(found, out r);

            // 最小化窗口的矩形是哨兵值（约 -32000 起，实测见到 -47976）。
            // 那种情况下宽高可能仍是正常值，只判断宽高会算出荒唐的点击坐标。
            //
            // 即使已在前面激活过窗口，UIA 的坐标也可能还没跟上（刚还原时树要一点时间稳定），
            // 所以这里按 Codex 的恢复流程重试一次：等待 → 重新定位 → 重新取矩形。
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool rectOk = !(r.Right <= r.Left || r.Bottom <= r.Top) &&
                              r.Left > -30000 && r.Top > -30000 &&
                              r.Left < 32000 && r.Top < 32000;
                if (rectOk) break;

                if (attempt == 0)
                {
                    // 重新激活并等待界面稳定，然后按描述符重新定位元素。
                    // 等待时间取 800ms：实测 400ms 时 UIA 仍在报最小化的哨兵矩形，
                    // 窗口从最小化还原到 UIA 更新坐标需要的时间比预期长。
                    RequireActivated(hwnd, "click_element");
                    System.Threading.Thread.Sleep(800);
                    RequireActivated(hwnd, "click_element");   // 再确认一次前台
                    AutomationElement again = FindByDescriptor(root, want, 0, 4000);
                    if (again != null) { found = again; GetElementRect(found, out r); }
                }
                else
                {
                    throw new InvalidOperationException(
                        "元素矩形无效（" + r.Left + "," + r.Top + " " +
                        (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "）。" +
                        "已尝试重新激活并重新定位一次仍失败，通常是窗口仍处于最小化或不可见状态：" + desc);
                }
            }

            int cx = (r.Left + r.Right) / 2;
            int cy = (r.Top + r.Bottom) / 2;

            MoveCursorAbsolute(cx, cy);
            SendMouseButton(right, 1);

            Native.POINT after;
            string actual = Native.GetCursorPos(out after) ? (after.X + "," + after.Y) : "?";

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("matched").Val(desc)
                .Key("method").Val("CoordinateClick")
                .Key("rect").Val(r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top))
                .Key("clickedX").Num(cx).Key("clickedY").Num(cy)
                .Key("cursorAfter").Val(actual)
                .Key("cursorMoved").Bool(true)
                .Key("button").Val(right ? "right" : "left")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }


        /// <summary>
        /// 描述"屏幕某一点下面是哪个 UIA 元素"，用于点击防呆。
        ///
        /// 返回一个简短的 JSON 片段（供 Jw 拼进去），或者 null（读不到）。
        /// 三个字段最有用：
        ///   · name —— 元素的可见文字，人一看就知道点的是什么
        ///   · role —— 控件类型（Button / Hyperlink / Text …）
        ///   · rect —— 它的矩形，能直接拿去算中心，不必再估
        /// </summary>
        private static string DescribePoint(int x, int y, out bool actionable)
        {
            actionable = false;
            try
            {
                System.Windows.Point pt = new System.Windows.Point(x, y);
                AutomationElement el = AutomationElement.FromPoint(pt);
                if (el == null) return null;

                string name = SnapshotOps.SafeStr(delegate { return el.Current.Name; });
                // ★ 保留带前缀的原始名（ControlType.Button）用于**判断**，
                //   只在输出时去掉前缀。
                //
                //   第一版这里直接 Replace 了再拿去和 "ControlType.Button" 比，
                //   于是永远不相等 —— "最大化" 按钮被报成"不是可交互控件"。
                //   判断和展示必须用同一个值，或者分开两个变量。
                string roleFull = SnapshotOps.SafeStr(delegate { return el.Current.ControlType.ProgrammaticName; });
                string role = roleFull == null ? null : roleFull.Replace("ControlType.", "");
                string aid = SnapshotOps.SafeStr(delegate { return el.Current.AutomationId; });
                System.Windows.Rect r;
                try { r = el.Current.BoundingRectangle; } catch { return null; }
                if (r.IsEmpty) return null;

                // 有没有"能点的意思"：有名字的可交互类型才算。
                // 只有窗口背景 / Pane 这类，说明调用方很可能点在空白处。
                bool named = !string.IsNullOrEmpty(name);
                bool interactive =
                    roleFull == "ControlType.Button" || roleFull == "ControlType.Hyperlink" ||
                    roleFull == "ControlType.MenuItem" || roleFull == "ControlType.TabItem" ||
                    roleFull == "ControlType.CheckBox" || roleFull == "ControlType.RadioButton" ||
                    roleFull == "ControlType.ComboBox" || roleFull == "ControlType.Edit" ||
                    roleFull == "ControlType.ListItem" || roleFull == "ControlType.TreeItem";
                actionable = named && interactive;

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"name\":").Append(name == null ? "null" : Jw.Str(name))
                  .Append(",\"role\":").Append(role == null ? "null" : Jw.Str(role))
                  .Append(",\"aid\":").Append(aid == null ? "null" : Jw.Str(aid))
                  .Append(",\"x\":").Append((int)r.X)
                  .Append(",\"y\":").Append((int)r.Y)
                  .Append(",\"w\":").Append((int)r.Width)
                  .Append(",\"h\":").Append((int)r.Height)
                  .Append(",\"enabled\":").Append(SnapshotOps.SafeBool(delegate { return el.Current.IsEnabled; }) ? "true" : "false")
                  .Append(",\"actionable\":").Append(actionable ? "true" : "false")
                  .Append("}");
                return sb.ToString();
            }
            catch (Exception e)
            {
                // 把原因带出去，而不是静默返回 null。
                // 静默 null 让我一开始以为"这些点上确实没有元素"，
                // 而实际上可能是 FromPoint 整体不可用。
                throw new InvalidOperationException(
                    "AutomationElement.FromPoint 失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 在窗口里找离给定点最近的"有名字的可交互元素"，用于告诉调用方
        /// "你可能想点的是这个"。只在点到空白处时用，所以不必精确。
        /// </summary>
        private static string NearestActionable(IntPtr hwnd, int x, int y)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return null;

                AutomationElement best = null;
                double bestDist = double.MaxValue;

                // 只找有名字的可交互类型，限制数量，避免在大界面上跑太久
                Condition cond = new PropertyCondition(AutomationElement.IsControlElementProperty, true);
                AutomationElementCollection all = root.FindAll(TreeScope.Descendants, cond);
                int scanned = 0;
                foreach (AutomationElement el in all)
                {
                    if (++scanned > 800) break;
                    string name = SnapshotOps.SafeStr(delegate { return el.Current.Name; });
                    if (string.IsNullOrEmpty(name)) continue;
                    string role = SnapshotOps.SafeStr(delegate { return el.Current.ControlType.ProgrammaticName; });
                    if (role == null) continue;
                    if (!(role == "ControlType.Button" || role == "ControlType.Hyperlink" ||
                          role == "ControlType.MenuItem" || role == "ControlType.TabItem" ||
                          role == "ControlType.CheckBox" || role == "ControlType.RadioButton" ||
                          role == "ControlType.ComboBox" || role == "ControlType.Edit" ||
                          role == "ControlType.ListItem")) continue;

                    System.Windows.Rect r;
                    try { r = el.Current.BoundingRectangle; } catch { continue; }
                    if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;

                    double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                    double d = Math.Sqrt((cx - x) * (cx - x) + (cy - y) * (cy - y));
                    if (d < bestDist) { bestDist = d; best = el; }
                }
                if (best == null) return null;

                string bname = SnapshotOps.SafeStr(delegate { return best.Current.Name; });
                string brole = SnapshotOps.SafeStr(delegate { return best.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });
                string baid = SnapshotOps.SafeStr(delegate { return best.Current.AutomationId; });
                System.Windows.Rect br = best.Current.BoundingRectangle;

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"name\":").Append(Jw.Str(bname))
                  .Append(",\"role\":").Append(Jw.Str(brole))
                  .Append(",\"aid\":").Append(baid == null ? "null" : Jw.Str(baid))
                  .Append(",\"x\":").Append((int)br.X)
                  .Append(",\"y\":").Append((int)br.Y)
                  .Append(",\"w\":").Append((int)br.Width)
                  .Append(",\"h\":").Append((int)br.Height)
                  .Append(",\"distance\":").Append((int)bestDist)
                  .Append("}");
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// whatis —— 查询某个坐标下面是哪个元素。用法：whatis &lt;x&gt; &lt;y&gt; [--hwnd H]
        ///
        /// 这是给"点之前先确认"用的：拿到元素名字和矩形，就能用描述符去点，
        /// 不必再靠估的坐标。
        /// </summary>
        private static int CmdWhatIs(string[] args)
        {
            // 它返回的是**界面内容**（元素名字、位置），所以和 tree / screenshot
            // 同一级别：急停时要停，白名单外的应用不读。
            // 守卫检查脚本把这条抓出来过 —— 我加命令时漏了。
            RequireNotHalted("whatis");

            List<string> pos = new List<string>();
            IntPtr target = ParseOptionalHwnd(args, pos);
            if (pos.Count < 2) throw new ArgumentException("whatis 需要 x y");
            int x = int.Parse(pos[0]);
            int y = int.Parse(pos[1]);

            // 没有给窗口就用坐标处的窗口来校验白名单
            if (target != IntPtr.Zero)
            {
                RequireAllowedWindow(target, "whatis");
            }
            else
            {
                IntPtr h = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
                IntPtr rt = h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
                if (rt != IntPtr.Zero) RequireAllowedWindow(rt, "whatis");
            }

            bool actionable = false;
            string el = null;
            string fromPointError = null;
            try
            {
                el = DescribePoint(x, y, out actionable);
            }
            catch (Exception e)
            {
                fromPointError = e.Message;
            }

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true).Key("x").Num(x).Key("y").Num(y);
            if (fromPointError != null)
            {
                j.Key("element").Val("null")
                 .Key("fromPointError").Val(fromPointError)
                 .Key("note").Val("AutomationElement.FromPoint 在这个环境里不可用（见 fromPointError）。");
            }
            else if (el == null)
            {
                j.Key("element").Val("null")
                 .Key("note").Val("这一点上读不到 UIA 元素（可能是自绘界面、视频画面，或完全在窗口外）");
            }
            else
            {
                j.Key("element").Raw(el);
                j.Key("actionable").Bool(actionable);
                j.Key("note").Val(actionable
                    ? "这一点上有可交互元素。**建议改用 desktop_click_element 传它的 name/role**，不要靠坐标。"
                    : "这一点上没有可交互元素（多半是背景/文字）。可直接用返回里的矩形算中心，或换一个位置。");
            }

            // 有目标窗口时，顺便给最近的元素，方便纠正偏差
            if (target != IntPtr.Zero)
            {
                string near = NearestActionable(target, x, y);
                j.Key("nearest").Val(near == null ? "null" : near);
            }

            j.End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>按描述符深度优先查找第一个匹配元素。</summary>
        // 计数器：确认描述符是否唯一。
        //
        // 原先遇到第一个匹配就直接返回，于是"同名按钮有两个"时，
        // 点的是谁是遍历顺序决定的——调用方以为点的是它想点的那个。
        // 这不报错，也不容易发现。
        private sealed class MatchTally
        {
            public int Count;
            public AutomationElement First;
            public int Limit = 2;      // 只要确认"是不是唯一"，找到 2 个就够
        }

        /// <summary>
        /// 搜索预算：**所有分支共享同一个计数器**，外加总时长上限。
        ///
        /// ── 为什么不能靠递归传参 ──
        ///
        /// 上一版每个分支传的是 budget - 1。那意味着预算**只随深度下降**：
        /// 一个宽树（同一个父节点下几千个兄弟）里，每个兄弟拿到的预算都一样，
        /// 谁都耗不尽它 —— 实际限制退化成"深度 × 宽度"，可以走到很远。
        ///
        /// 加了唯一性检查之后更要紧：为了确认"是否唯一"，得找到第二个匹配
        /// 才停，遍历的节点比以前更多。所以这里改成共享计数 + 时间上限，
        /// 两条都要满足才继续。
        ///
        /// 注意：这只能拦住"我们自己还要不要继续找"，**拦不住一个已经阻塞在
        /// COM 调用里的 UIA 查询**。那种情况只能靠进程级超时（helper 是短进程，
        /// 插件那边有 30 秒硬超时兜底）。
        /// </summary>
        private sealed class SearchBudget
        {
            public int Remaining;
            public long DeadlineMs;
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

            public bool Take()
            {
                if (Remaining <= 0) return false;
                if (_clock.ElapsedMilliseconds >= DeadlineMs) return false;
                Remaining--;
                return true;
            }
        }

        /// <summary>
        /// 按描述符查找元素，并统计匹配数量。
        ///
        /// 返回第一个匹配（供调用方使用），同时通过 tally 告知是否唯一。
        /// 找到 tally.Limit 个就停止，不必遍历完整棵树 —— 我们只需要
        /// "是否唯一"这个信息，不需要全部候选。
        /// </summary>
        private static AutomationElement FindByDescriptor(
            AutomationElement el, Dictionary<string, string> want,
            int depth, SearchBudget budget, MatchTally tally)
        {
            if (depth > 64) return null;
            if (tally != null && tally.Count >= tally.Limit) return tally.First;
            // 共享预算：所有兄弟分支从同一个计数器里扣
            if (budget != null && !budget.Take()) return tally != null ? tally.First : null;

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
            if (match)
            {
                if (tally != null)
                {
                    tally.Count++;
                    if (tally.First == null) tally.First = el;
                    return tally.First;
                }
                return el;
            }

            AutomationElement child = null;
            try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); } catch { }
            while (child != null)
            {
                AutomationElement hit = FindByDescriptor(child, want, depth + 1, budget, tally);
                if (tally != null && tally.Count >= tally.Limit) return tally.First;
                if (tally == null && hit != null) return hit;
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
            }
            return tally != null ? tally.First : null;
        }

        /// <summary>兼容旧调用：不带计数，返回第一个匹配。</summary>
        private static AutomationElement FindByDescriptor(
            AutomationElement el, Dictionary<string, string> want, int depth, int budget)
        {
            return FindByDescriptor(el, want, depth,
                new SearchBudget { Remaining = budget, DeadlineMs = 5000 }, null);
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
            if (args.Length < 4) throw new ArgumentException("set_value 需要 hwnd <index|描述符> value");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            RequireAllowedWindow(hwnd, "set_value");
            // 选择器既可以是读树给出的序号，也可以是描述符（role=/name=/aid=）。
            //
            // ── 为什么加描述符 ──
            //
            // 序号是**遍历顺序**，不是一个稳定的身份。树一变（加载出新节点、
            // 折叠展开、异步插入），同一个序号就指向别的控件。而"写入后回读一致"
            // 只能证明**写进了这个控件**，证明不了**写进了想要的那个**——
            // 这是最坏的一种错：看起来成功，实际改错了东西。
            //
            // 描述符按 role/name/aid 重新定位，跨调用稳定得多。序号仍然支持
            // （同一次读取后立刻用是可接受的），但描述符是更可靠的选择。
            string selector = args[2];
            string value = args[3];
            bool byDescriptor = selector.IndexOf('=') > 0;
            int index = -1;
            string label = "";

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            // 在同一次调用内重新抓一份快照再写：一次性进程模型下跨调用持不住快照，
            // 所以"索引仍然有效"只能靠这次重新绑定保证。
            // set_value 走 UIA 不注入，但内部要 SetFocus；在非前台窗口上聚焦可能无效，
            // 因此同样先激活（与 Codex 的"输入类方法自动激活"一致）。
            RequireActivated(hwnd, "set_value");

            AutomationElement el = null;
            if (byDescriptor)
            {
                Dictionary<string, string> want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in selector.Split('/'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) want[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                }
                if (want.Count == 0)
                    throw new ArgumentException("描述符为空，例如 role=Edit/name=搜索");
                label = "元素 [" + selector + "]";
                MatchTally tally = new MatchTally();
                el = FindByDescriptor(root, want, 0, new SearchBudget { Remaining = 4000, DeadlineMs = 5000 }, tally);
                if (tally.Count > 1)
                    return FailJson("AmbiguousTarget",
                        "描述符匹配到多个元素（至少 " + tally.Count + " 个）：" + selector +
                        "；写进哪一个都不会报错，所以拒绝。请加更具体的条件。");
                if (el == null)
                    return FailJson("NotFound", "未找到匹配元素：" + selector + "；先重读一次树确认它还在");
            }
            else
            {
                index = int.Parse(selector);
                if (index < 0)
                    return FailJson("BadIndex", "element index must be >= 0, got " + index);
                label = "element " + index;

                // 只为绑定这一个索引而抓快照：抓到 index+1 个即可，超出 index 的部分不需要。
                AutomationElement target = null;
            if (byDescriptor)
            {
                Dictionary<string, string> want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in selector.Split('/'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) want[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                }
                if (want.Count == 0)
                    throw new ArgumentException("描述符为空，例如 role=Button/name=保存");
                label = "元素 [" + selector + "]";
                MatchTally tally = new MatchTally();
                target = FindByDescriptor(root, want, 0, new SearchBudget { Remaining = 4000, DeadlineMs = 5000 }, tally);
                if (tally.Count > 1)
                    return FailJson("AmbiguousTarget",
                        "描述符匹配到多个元素（至少 " + tally.Count + " 个）：" + selector +
                        "；在错的控件上做动作可能直接触发别的事情，所以拒绝。请加更具体的条件。");
                if (target == null)
                    return FailJson("NotFound", "未找到匹配元素：" + selector);
            }
            else
            {
                index = int.Parse(selector);
                if (index < 0)
                    return FailJson("BadIndex", "element index must be >= 0, got " + index);
                label = "element " + index;
            }

            Snapshot snap = byDescriptor ? null
                : SnapshotOps.Capture(root, hwnd, index + 1, DefaultMaxDepth, DefaultMaxChildren);
                if (!snap.Map.TryGetValue(index, out el))
                    return FailJson("StaleSnapshot",
                        label + " is not present in window " + hwnd +
                        " (" + snap.Total + " elements exist); re-read the tree");
            }

            if (SnapshotOps.SafeBool(delegate { return el.Current.IsPassword; }))
                return FailJson("Refused",
                    label + " is a password field; refusing to set its value");

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
                            label + " (" + role + ") is read-only; cannot set its value");

                    try { el.SetFocus(); } catch { }     // Codex 也是先聚焦
                    System.Threading.Thread.Sleep(40);

                    string before = null;
                    try { before = vp.Current.Value; } catch { }

                    vp.SetValue(value);

                    // 回读校验：一次性进程模型下没有跨调用的可比基准，
                    // 唯一能自证"确实写进去了"的办法就是写完立刻读回来。
                    System.Threading.Thread.Sleep(60);
                    string after = null;
                    try { after = vp.Current.Value; } catch { }

                    return OkJson(hwnd, index, role, "ValuePattern", value,
                        before, after, string.Equals(after, value, StringComparison.Ordinal));
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
                            label + " (" + role + ") is read-only (range); cannot set its value");

                    double d;
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out d))
                        return FailJson("BadValue",
                            label + " exposes RangeValuePattern; its value must be a number, got: " + value);

                    // 先聚焦再读区间：实测未聚焦时 Current.Minimum/Maximum 可能返回 0/0，
                    // 而控件真实范围是 0..100（NumericUpDown）。若不重读，任何值都会
                    // 被误判成越界——那是把"我读不到"错报成"你越界了"。
                    try { el.SetFocus(); } catch { }
                    System.Threading.Thread.Sleep(60);

                    double min = rv.Current.Minimum, max = rv.Current.Maximum;

                    // [0,0] 是"读不到区间"的特征值，不是真的只允许 0
                    if (min == 0 && max == 0)
                    {
                        try
                        {
                            object p2;
                            if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out p2))
                            {
                                RangeValuePattern rv2 = (RangeValuePattern)p2;
                                min = rv2.Current.Minimum; max = rv2.Current.Maximum;
                            }
                        }
                        catch { }

                        if (min == 0 && max == 0)
                            return FailJson("RangeUnavailable",
                                label + " exposes RangeValuePattern but reports an empty range [0, 0]; " +
                                "the control likely does not publish its bounds to UIA. Refusing to guess.");
                    }

                    if (d < min || d > max)
                        return FailJson("OutOfRange",
                            "value " + d + " is outside the allowed range [" + min + ", " + max + "]");

                    double beforeD = double.NaN;
                    try { beforeD = rv.Current.Value; } catch { }

                    rv.SetValue(d);

                    System.Threading.Thread.Sleep(60);
                    double afterD = double.NaN;
                    try { afterD = rv.Current.Value; } catch { }

                    return OkJson(hwnd, index, role, "RangeValuePattern", value,
                        beforeD.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        afterD.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Math.Abs(afterD - d) < 0.0001);
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
            if (args.Length < 3) throw new ArgumentException("do_action 需要 hwnd <index|描述符> action");
            IntPtr hwnd = new IntPtr(long.Parse(args[1]));
            RequireAllowedWindow(hwnd, "do_action");

            // 与 set_value 一致：选择器可以是序号，也可以是描述符。
            //
            // 序号是遍历顺序，树一变就指向别的控件。动作比写入更危险——
            // 在错的控件上做 Raise/Expand 可能直接触发别的事情，
            // 而且做完就收不回来。
            string selector = args[2];
            bool byDescriptor = selector.IndexOf('=') > 0;
            int index = -1;
            string label = "";
            string wanted = NormalizeAction(args[3]);

            if (wanted == null)
                return FailJson("BadAction",
                    "unsupported secondary action: " + args[3] +
                    "; expected Raise, Scroll Up, Scroll Down, Scroll Left, Scroll Right, Expand, or Collapse");

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            AutomationElement el = null;
            Snapshot snap = null;
            List<string> available = null;

            if (byDescriptor)
            {
                Dictionary<string, string> want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in selector.Split('/'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) want[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                }
                if (want.Count == 0)
                    throw new ArgumentException("描述符为空，例如 role=Button/name=保存");
                label = "元素 [" + selector + "]";
                MatchTally tally = new MatchTally();
                el = FindByDescriptor(root, want, 0, new SearchBudget { Remaining = 4000, DeadlineMs = 5000 }, tally);
                if (tally.Count > 1)
                    return FailJson("AmbiguousTarget",
                        "描述符匹配到多个元素（至少 " + tally.Count + " 个）：" + selector +
                        "；在错的控件上做动作可能直接触发别的事，所以拒绝。请加更具体的条件。");
                if (el == null)
                    return FailJson("NotFound", "未找到匹配元素：" + selector);
                // 描述符模式没有快照，动作可用性按元素现读（见下面的兜底）
            }
            else
            {
                index = int.Parse(selector);
                if (index < 0)
                    return FailJson("BadIndex", "element index must be >= 0, got " + index);
                label = "element " + index;
                snap = SnapshotOps.Capture(root, hwnd, index + 1, DefaultMaxDepth, DefaultMaxChildren);
                if (!snap.Map.TryGetValue(index, out el))
                    return FailJson("StaleSnapshot",
                        label + " is not present in window " + hwnd + "; re-read the tree");
                snap.Actions.TryGetValue(index, out available);
            }

            // 序号模式用快照里缓存的可用动作；描述符模式按元素现读。
            // 两者都为 null 时不做预检，让下面的 Pattern 尝试自己失败并报出来。
            if (available != null && available.Count > 0 && !available.Contains(wanted))
                return FailJson("ActionNotAvailable",
                    "secondary action '" + wanted + "' is not available for " + label +
                    "; available actions: " + string.Join(", ", available.ToArray()));

            string role = SnapshotOps.SafeStr(delegate { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });

            try
            {
                object p;

                if (wanted == "Raise")
                {
                    if (!el.TryGetCurrentPattern(WindowPattern.Pattern, out p))
                        return FailJson("PatternLost", label + " no longer exposes WindowPattern");
                    // 置顶：先还原再前置，避免对已最小化的窗口直接 SetWindowVisualState 失败
                    ((WindowPattern)p).SetWindowVisualState(WindowVisualState.Normal);
                }
                else if (wanted.StartsWith("Scroll "))
                {
                    if (!el.TryGetCurrentPattern(ScrollPattern.Pattern, out p))
                        return FailJson("PatternLost", label + " no longer exposes ScrollPattern");
                    ScrollPattern sp = (ScrollPattern)p;
                    if (wanted == "Scroll Up") sp.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement);
                    else if (wanted == "Scroll Down") sp.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    else if (wanted == "Scroll Left") sp.Scroll(ScrollAmount.LargeDecrement, ScrollAmount.NoAmount);
                    else sp.Scroll(ScrollAmount.LargeIncrement, ScrollAmount.NoAmount);
                }
                else
                {
                    if (!el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p))
                        return FailJson("PatternLost", label + " no longer exposes ExpandCollapsePattern");
                    ExpandCollapsePattern ec = (ExpandCollapsePattern)p;
                    // ExpandCollapsePattern 的真实 API 是 Expand() / Collapse()，
                    // 没有可写属性（反射确认过，别再凭记忆写）。
                    if (wanted == "Expand") ec.Expand();
                    else ec.Collapse();
                }

                Jw j = new Jw();
                j.Obj().Key("ok").Bool(true)
                    .Key("hwnd").Num(hwnd.ToInt64())
                    .Key("index").Num(index).Key("selector").Val(selector)
                    .Key("role").Val(role)
                    .Key("action").Val(wanted)
                    .Key("revision").Num(snap != null ? snap.Revision : 0)
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

        /// <summary>
        /// 成功回包。before/after 为回读校验的结果：
        /// 一次性进程模型下没有跨调用的可比基准，唯一的自证方式就是写完立刻读回来。
        /// verified=false 表示"调用成功但值没变"——调用方应当据此怀疑而不是相信。
        /// </summary>
        private static int OkJson(IntPtr hwnd, int index, string role, string via, string value,
                                  string before, string after, bool verified)
        {
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("index").Num(index)
                .Key("role").Val(role)
                .Key("via").Val(via)
                .Key("requested").Val(value)
                .Key("before").Val(before)
                .Key("after").Val(after)
                .Key("verified").Bool(verified)
             .End();
            Console.WriteLine(j.ToString());
            return verified ? 0 : 4;      // 4 = 调用成功但回读不一致
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
            if (args.Length < 3) throw new ArgumentException("type 需要 hwnd 与文本参数");
            IntPtr typeHwnd = new IntPtr(long.Parse(args[1]));
            RequireAllowedWindow(typeHwnd, "type");
            RequireActivated(typeHwnd, "type");
            string text = args[2];

            // 用 SendInput + KEYEVENTF_UNICODE，不用 keybd_event。
            //
            // 原因：keybd_event 的扫描码参数是 BYTE，写 `(byte)c` 会把任何
            // 非 ASCII 字符截断——实测输入「键盘输入成功XY」得到 "BBB.ØeXY"，
            // 中文全毁且长度变短。'键'(U+952E) 被截成 0x2E，高位丢失。
            //
            // SendInput 的 KEYBDINPUT.wScan 是 USHORT，能装下整个 BMP，
            // 因此中文、日文、emoji（BMP 内）都能原样送达。
            // 输入过程中**必须重新检查急停**。
            //
            // 上一版只在命令入口查一次闸门。而文本输入是按 8ms/字符串行发出的，
            // 一段 5000 字的文本要 40 秒 —— 用户在这 40 秒里按 ESC，
            // 输入会照常打完。急停的意义恰恰是"立刻停"，不是"下一条命令才停"。
            //
            // 每 16 个字符查一次：读一个文件约几十微秒，对总体速度没有影响，
            // 而 16 字符 ≈ 128ms，人手按 ESC 的反应时间远长于它。
            int typedChars = 0;
            foreach (char c in text)
            {
                if ((typedChars % 16) == 0 && IsHalted())
                    return FailJson("Halted",
                        "输入被急停中断：已输入 " + typedChars + " / " + text.Length +
                        " 个字符。闸门是拉下状态，其余内容没有发送。");

                Native.INPUT[] pair = new Native.INPUT[2];

                pair[0].type = Native.INPUT_KEYBOARD;
                pair[0].ki.wVk = 0;
                pair[0].ki.wScan = (ushort)c;
                pair[0].ki.dwFlags = Native.KEYEVENTF_UNICODE;

                pair[1].type = Native.INPUT_KEYBOARD;
                pair[1].ki.wVk = 0;
                pair[1].ki.wScan = (ushort)c;
                pair[1].ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;

                uint sent = Native.SendInput(2, pair, Marshal.SizeOf(typeof(Native.INPUT)));
                if (sent == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        "文本输入在第 " + (text.IndexOf(c) + 1) + " 个字符处失败 (Win32 错误 " + err + ": " +
                        new System.ComponentModel.Win32Exception(err).Message + ")。");
                }
                typedChars++;
                System.Threading.Thread.Sleep(8);
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
            if (args.Length < 3) throw new ArgumentException("press_key 需要 hwnd 与按键参数");
            IntPtr keyHwnd = new IntPtr(long.Parse(args[1]));
            RequireAllowedWindow(keyHwnd, "press_key");
            RequireActivated(keyHwnd, "press_key");
            string[] parts = args[2].Split('+');
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

            // 发之前记录前台窗口，作为"键会发给谁"的凭据。
            // RequireActivated 已经把目标激活了，这里再取一次是为了**报告**，
            // 不是校验——让调用方能核实"键发给了哪个窗口"。
            IntPtr fgBefore = Native.GetForegroundWindow();

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

            // ── 返回可核实的信息，而不是只说 ok ──
            //
            // 这里原来有一处真 bug：`Key("chord").Val(args[1])` 回显的是
            // **窗口句柄**（args[1]），而不是按键（args[2]）。
            // 于是 `press_key 460068 Space` 会返回 `{"ok":true,"chord":"460068"}`，
            // 看起来像"参数被错位取用"，把排查方向带骗了整整一轮。
            // 插件层还忠实转发了这个错值，屏幕上显示
            // 「已向窗口 328194 发送按键 328194」。
            //
            // 光修回显还不够。keybd_event **没有返回值**，所以这里无法判断
            // 按键是否真的被应用接受——那就把能给的都给出，让调用方自己核实：
            //   · chord     —— 原样的和弦字符串（这次是对的）
            //   · vk        —— 主键的虚拟键码
            //   · mods      —— 修饰键的虚拟键码
            //   · fg        —— 发键时的前台窗口（键实际发给了它）
            //   · verified  —— 固定为 false，并解释原因
            // 诚实地说明"无法核实"比再报一个 ok 有用。
            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("chord").Val(args[2])
                .Key("vk").Val("0x" + main.ToString("X2"))
                .Key("mods").Val(string.Join(",", new List<byte>(mods)
                    .ConvertAll(x => "0x" + x.ToString("X2")).ToArray()))
                .Key("foreground").Num(fgBefore.ToInt64())
                .Key("sentToTarget").Bool(fgBefore == keyHwnd)
                .Key("verified").Bool(false)
                .Key("note").Val(
                    "已调用 keybd_event 发送，但它不返回成功/失败，" +
                    "因此无法确认目标应用是否接受。要核实请观察界面变化" +
                    "（例如重读一次无障碍树或截图）。")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>
        /// 把光标移到绝对屏幕坐标（物理像素）。
        ///
        /// 历史注记：本方法原先用 SendInput 的绝对模式，理由写在旧注释里——
        /// 「SetCursorPos 被拒并报 ERROR_FILE_NOT_FOUND(2)/ERROR_PATH_NOT_FOUND(3)，
        /// 而 SendInput 成功」。那个理由**是错的**，而且正是它把光标送错了位置：
        /// SendInput 的绝对坐标会被 DPI 再缩放一次，落点恒为目标的 0.667 倍。
        /// SetCursorPos 报错的那次是另一个上下文（非交互式）造成的误判。
        /// 详见下面的实测数据。
        /// </summary>
        private static void MoveCursorAbsolute(int x, int y)
        {
            // 用 SetCursorPos，不用 SendInput 的绝对模式。
            //
            // ── 下面这些是**一台机器上的一次观察**，不是跨机定律 ──
            //
            // 那台机器：SYSTEM_AWARE、LOGPIXELSX=144（150% 缩放）、SM_CXSCREEN=2240。
            //   · SetCursorPos(1500,900) → 落点 1500,900，比值 1.000。
            //   · SendInput + MOUSEEVENTF_ABSOLUTE + VIRTUALDESK（按 65535 归一化）
            //     在同样的点上落在 2/3 处，比值恒为 0.667。
            //
            // 我当初从这里推出"SendInput 的绝对坐标必定被二次缩放，SetCursorPos
            // 是唯一正确组合"。**那是过度归纳**——第三方审查指出得对：一次单机
            // 实验支持不了"必定"这个词，而且换一台机器、换一个 DPI awareness
            // 级别，这个比值完全可能不同（比如 1.0、0.5、或随显示器而变）。
            //
            // 所以这里的取舍是**工程上的**，不是物理上的：
            //   · 本进程现在由 manifest 声明 PerMonitorV2，坐标按物理像素走；
            //   · SetCursorPos 接受的就是屏幕物理坐标，不需要任何归一化换算；
            //   · 参数越少越不容易错——SendInput 那条路要同时管
            //     0..65535 归一化、主屏还是 VIRTUALDESK、虚拟桌面原点。
            //
            // **如果换机器发现落点系统性偏移**：先量（连续几个点的实际落点），
            // 判断是比例偏移还是固定偏移，再决定往哪查。不要照搬这里的 0.667，
            // 也不要直接乘一个系数去"补偿"——那会在别的机器上造成新的偏移。
            if (!Native.SetCursorPos(x, y))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    "SetCursorPos(" + x + "," + y + ") 失败 (Win32 错误 " + err + ": " +
                    new System.ComponentModel.Win32Exception(err).Message + ")。");
            }
            System.Threading.Thread.Sleep(50);

            // 回读校验：不能只信返回值。
            // 之前的实现把"我传进去的值"当作 cursorAfter 报出去，于是即使落点错了
            // 也显示成功——那是自欺。这里读的是屏幕上的真实位置。
            Native.POINT p;
            if (Native.GetCursorPos(out p))
            {
                if (Math.Abs(p.X - x) > 2 || Math.Abs(p.Y - y) > 2)
                    throw new InvalidOperationException(
                        "光标未落到目标位置：请求 (" + x + "," + y + ") 实际 (" + p.X + "," + p.Y + ")。" +
                        "先在几个不同位置复测：若偏差与位置成比例（比值恒定），" +
                        "多半是 DPI awareness 与坐标基准不一致；若近似固定偏移，" +
                        "查窗口边框/截图原点。不要直接乘系数补偿——那会在别的机器上造成新的偏移。");
            }
        }

        /// <summary>发送一次鼠标按键（按下 + 抬起）。</summary>
        /// <summary>按钮名 → (按下, 抬起) 两个事件标志。</summary>
        private static void ButtonFlags(string button, out uint down, out uint up)
        {
            string b = (button ?? "left").ToLowerInvariant();
            if (b == "right") { down = Native.MOUSEEVENTF_RIGHTDOWN; up = Native.MOUSEEVENTF_RIGHTUP; return; }
            if (b == "middle") { down = Native.MOUSEEVENTF_MIDDLEDOWN; up = Native.MOUSEEVENTF_MIDDLEUP; return; }
            if (b != "left") throw new ArgumentException("button 只能是 left / right / middle：" + button);
            down = Native.MOUSEEVENTF_LEFTDOWN; up = Native.MOUSEEVENTF_LEFTUP;
        }

        private static void SendMouseButton(string button, int clickCount)
        {
            uint down, up;
            ButtonFlags(button, out down, out up);

            int n = Math.Max(1, clickCount);
            for (int i = 0; i < n; i++)
            {
                // 双击的两次之间要短：系统的双击判据看间隔（默认 500ms），
                // 但 40ms 那种"间隔很快的两次单击"在多数应用里会被当成双击。
                // 第一次按下前多等一点，让前一个动作的界面响应先到位。
                System.Threading.Thread.Sleep(i == 0 ? 40 : 30);
                Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(n > 1 ? 30 : 40);
                Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            }
        }

        /// <summary>兼容旧调用（只分左右）。</summary>
        private static void SendMouseButton(bool right, int clickCount)
        {
            SendMouseButton(right ? "right" : "left", clickCount);
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
            IntPtr hwnd = ParseHwnd(args, "tree");
            // 急停检查必须是第一件事——**读界面内容也要受它约束**。
            //
            // 这里原先漏了：闸门拉下后 tree 照样返回整个界面文字，
            // 而用户按 ESC 的意图正是别再碰我的电脑。界面消失了、
            // 操作被拒了，读却还在跑——那是最不该留的一条缝。
            RequireNotHalted("tree");
            // 白名单校验：读界面内容同样要授权（树里有界面上全部文字）。
            RequireAllowedWindow(hwnd, "tree");

            // 位置参数 hwnd [maxNodes]，另有可选开关 --depth / --children。
            // 三个上限都是为了不在怪应用上挂死，也为了避免一次取回上千个活 RCW。
            int maxNodes = DefaultMaxNodes;
            int maxDepth = DefaultMaxDepth;
            int maxChildren = DefaultMaxChildren;
            if (args.Length > 2 && !args[2].StartsWith("--")) maxNodes = int.Parse(args[2]);
            for (int a = 2; a < args.Length - 1; a++)
            {
                if (args[a] == "--depth") maxDepth = int.Parse(args[a + 1]);
                else if (args[a] == "--children") maxChildren = int.Parse(args[a + 1]);
            }

            if (maxNodes < 1) throw new ArgumentException("maxNodes 必须 >= 1");
            if (maxDepth < 0 || maxDepth > 128) throw new ArgumentException("--depth 允许 0..128");
            if (maxChildren < 1 || maxChildren > 2000) throw new ArgumentException("--children 允许 1..2000");

            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) throw new InvalidOperationException("无法从该窗口句柄取得无障碍元素");

            // 树文本、索引表、次要动作表全部来自这一次遍历——它们必须同源，
            // 否则索引会和元素对不上号。
            Snapshot snap = SnapshotOps.Capture(root, hwnd, maxNodes, maxDepth, maxChildren);

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

            // ── 树的"可用性"度量 ──
            //
            // 为什么要量它：部分打包应用（Electron、WinUI/XAML）会把界面暴露成
            // 一大片无名的 Pane，树上看着有几百个节点，实际上**没有一个能用来
            // 定位或描述**。模型拿到这种树会以为"读到了界面"，然后基于空洞的
            // 信息做判断——问题不是"树不好用"，而是"树不好用的时候没人说"。
            //
            // 所以统计两件事：
            //   · 有多少节点**带名字或文本**（有内容的）
            //   · 有多少节点是**可交互角色**（Button/Edit/ListItem/MenuItem 等，
            //     这些才能写进元素描述符被 click_element 使用）
            // 两者都低就说明这棵树没有信息量，应当改用截图。
            Dictionary<string, int> roleCount = new Dictionary<string, int>();
            int named = 0;
            foreach (KeyValuePair<int, AutomationElement> kv in snap.Map)
            {
                string role = SnapshotOps.SafeStr(delegate
                {
                    return kv.Value.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                });
                if (string.IsNullOrEmpty(role)) role = "?";
                int c;
                roleCount.TryGetValue(role, out c);
                roleCount[role] = c + 1;

                string nm = SnapshotOps.SafeStr(delegate { return kv.Value.Current.Name; });
                if (!string.IsNullOrEmpty(nm)) named++;
            }

            int actionable = 0;
            string[] actionableRoles = new string[]
            {
                "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "Hyperlink",
                "ListItem", "MenuItem", "TabItem", "TreeItem", "Slider", "Spinner",
                "SplitButton", "DataItem", "Document", "Text",
            };
            foreach (string r in actionableRoles)
            {
                int c;
                if (roleCount.TryGetValue(r, out c)) actionable += c;
            }

            // 树是否值得用：要么有可交互元素，要么有带名字的节点。
            // 两个都少就意味着这棵树描述不了界面。
            bool usable = actionable >= 5 || named >= 12;

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("revision").Num(snap.Revision)
                .Key("total").Num(snap.Total)
                .Key("listed").Num(snap.Listed)
                .Key("truncated").Bool(snap.Truncated)
                .Key("named").Num(named)
                .Key("actionable").Num(actionable)
                .Key("usable").Bool(usable)
                .Key("roles").Val(string.Join(", ",
                    new List<string>(SortedRoles(roleCount, 12)).ToArray()))
                .Key("tree").Val(tree)
             .End();
            Console.WriteLine(j.ToString());
            return 0;
        }

        /// <summary>按数量降序取前 N 个"角色=数量"，供质量度量展示。</summary>
        private static IEnumerable<string> SortedRoles(Dictionary<string, int> counts, int top)
        {
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(counts);
            list.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            int n = 0;
            foreach (KeyValuePair<string, int> kv in list)
            {
                if (n++ >= top) break;
                yield return kv.Key + "=" + kv.Value;
            }
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
