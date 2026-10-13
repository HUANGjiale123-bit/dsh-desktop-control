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

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

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
                .Key("clr").Val(Environment.Version.ToString())
                .Key("os").Val(Environment.OSVersion.VersionString)
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
            if (args.Length < 3) throw new ArgumentException("click 需要 x y 参数（桌面绝对物理像素）");
            int x = int.Parse(args[1]);
            int y = int.Parse(args[2]);
            bool right = args.Length > 3 && args[3] == "right";

            if (!Native.SetCursorPos(x, y))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    "SetCursorPos 失败 (Win32 错误 " + err + ": " + new System.ComponentModel.Win32Exception(err).Message + ")。" +
                    "通常表示本进程没有交互式输入桌面的访问权——" +
                    "例如运行在受限窗口站、服务会话或沙箱中。" +
                    "把本程序作为 DSH 插件的宿主子进程运行时才有该权限。");
            }

            uint down = right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN;
            uint up = right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP;

            System.Threading.Thread.Sleep(60);
            Native.mouse_event(down, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            Native.mouse_event(up, 0, 0, 0, IntPtr.Zero);

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("clickedX").Num(x).Key("clickedY").Num(y)
                .Key("button").Val(right ? "right" : "left")
             .End();
            Console.WriteLine(j.ToString());
            return 0;
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
                string exe = null;
                try { exe = System.Diagnostics.Process.GetProcessById((int)pid).MainModule.FileName; }
                catch { }

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

            Elements.Clear();
            _nextElementId = 1;

            // 用 FindAll 一次性取整棵子树，而不是靠 TreeWalker 逐层下钻。
            // TreeWalker 在部分应用（尤其打包应用/XAML 岛）上会在中途停住，
            // 只拿到顶层几个 Pane；FindAll(Subtree) 取的是 UIA 视图的完整集合。
            AutomationElementCollection all =
                root.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition);

            int total = all.Count;
            int limit = Math.Min(total, maxNodes);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < limit; i++)
            {
                AutomationElement el = all[i];
                string id = Register(el);
                string role = Safe(delegate { return el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); });
                string name = Safe(delegate { return el.Current.Name; });
                string value = Safe(delegate
                {
                    if (el.Current.IsPassword) return "<password>";
                    return el.GetCurrentPropertyValue(ValuePattern.ValueProperty, true) as string;
                });

                Native.RECT r;
                GetElementRect(el, out r);

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
                    if (role == "Document" || role == "Edit")
                    {
                        object o;
                        if (el.TryGetCurrentPattern(TextPattern.Pattern, out o))
                        {
                            string t = ((TextPattern)o).DocumentRange.GetText(500);
                            if (!string.IsNullOrEmpty(t))
                                sb.Append("    text: ").Append(Trim(t, 320)).Append('\n');
                        }
                    }
                }
                catch { }
            }
            if (total > limit) sb.Append("… 另有 ").Append(total - limit).Append(" 个元素未列出\n");

            Jw j = new Jw();
            j.Obj().Key("ok").Bool(true)
                .Key("hwnd").Num(hwnd.ToInt64())
                .Key("view").Val("findall-subtree")
                .Key("total").Num(total)
                .Key("listed").Num(limit)
                .Key("tree").Val(sb.ToString().TrimEnd())
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
