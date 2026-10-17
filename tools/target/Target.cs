// dsc-target.exe —— 受控测试靶。**只用于测试，不随插件发布。**
//
// ── 为什么需要它 ──
//
// 之前的测试拿"当前屏幕上某个够大的窗口"当靶子，后果有两个：
//   1. 不可核对。点完之后无法证明"点到的就是我以为那个按钮"，
//      只能看返回的文字说成功了 —— 而那正是我一直在修的那类问题。
//   2. 不稳定。窗口会移动、会被遮挡、会被用户关掉，于是同一个测试
//      有时通过有时失败，而失败原因与代码无关。
//
// 第三方审查明确要求："测试程序要提供可核对的目标 ID、按钮命中计数、
// 输入内容和事件日志；不要随机选用户正在使用的窗口来证明点击正确。"
//
// ── 它怎么做到"可核对" ──
//
// 每个控件有一个**稳定的 name**（Alpha / Beta / Duplicate / Input / Choice），
// 每次被操作，它把自己**当时真的收到了什么**追加到日志文件（JSON Lines）：
//
//   {"seq":1,"event":"click","id":"Alpha","x":40,"y":20}
//   {"seq":2,"event":"value_changed","id":"Input","value":"hello"}
//   {"seq":3,"event":"selection","id":"Choice","value":"B"}
//
// 测试读这份日志，和"我以为发生了什么"对比。日志来自**靶子自己**，
// 不是来自被测试的插件 —— 这点是关键：证据必须由被测系统之外产生。
//
// ── 用法 ──
//
//   dsc-target.exe [--log <路径>] [--size WxH] [--title <标题>]
//
// 默认日志：%TEMP%\dsc-target-<pid>.jsonl
// 启动后会在日志里写一行 {"event":"ready","hwnd":...}，测试据此知道可以开始了。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DscTarget
{
    internal static class Program
    {
        internal static string LogPath;
        private static int _seq;
        private static readonly object Lock = new object();

        /// <summary>
        /// 追加一行 JSON 到日志。手写而不是引 JSON 库：
        /// 这个程序要在和 helper 相同的工具链下编译（.NET Framework + C# 5），
        /// 少一个依赖就少一处装机问题。
        /// </summary>
        internal static void Log(string json)
        {
            try
            {
                lock (Lock)
                {
                    _seq++;
                    string line = "{\"seq\":" + _seq + "," + json + "}";
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写不进去也不能让靶子崩 —— 否则测试会看到"窗口消失"
                // 而不是"日志写不进去"，方向又错了。
            }
        }

        internal static string Escape(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string sizeSpec = null;
            string title = "dsc-target 受控测试靶";

            LogPath = Path.Combine(Path.GetTempPath(), "dsc-target-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".jsonl");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--log" && i + 1 < args.Length) LogPath = args[++i];
                else if (args[i] == "--size" && i + 1 < args.Length) sizeSpec = args[++i];
                else if (args[i] == "--title" && i + 1 < args.Length) title = args[++i];
            }

            try
            {
                string dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 每次启动清空：旧日志会让"这次点了几次"变得不可判断
                File.WriteAllText(LogPath, "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("无法写日志文件: " + e.Message);
                return 2;
            }

            int w = 640, h = 480;
            if (!string.IsNullOrEmpty(sizeSpec))
            {
                string[] parts = sizeSpec.ToLowerInvariant().Split('x');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], out w);
                    int.TryParse(parts[1], out h);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Form f = new Form();
            f.Text = title;
            f.ClientSize = new Size(w, h);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(80, 80);
            f.MinimizeBox = true;
            f.MaximizeBox = false;

            // ── 按钮区：每个有稳定 name，点击即记日志 ──
            AddButton(f, "Alpha", 20, 20, delegate { Log("\"event\":\"click\",\"id\":\"Alpha\""); });
            AddButton(f, "Beta", 20, 60, delegate { Log("\"event\":\"click\",\"id\":\"Beta\""); });

            // 两个同名按钮：用来验证"描述符匹配到多个时必须拒绝，而不是默认第一个"
            AddButton(f, "Duplicate", 20, 100, delegate { Log("\"event\":\"click\",\"id\":\"Duplicate\",\"which\":1"); });
            AddButton(f, "Duplicate", 20, 140, delegate { Log("\"event\":\"click\",\"id\":\"Duplicate\",\"which\":2"); });
            // 上面两个的 AutomationId 不同，便于测试构造唯一描述符
            f.Controls[f.Controls.Count - 2].Name = "dup1";
            f.Controls[f.Controls.Count - 1].Name = "dup2";

            // ── 文本框：验证 set_value 的写入内容 ──
            Label lblIn = new Label();
            lblIn.Text = "Input";
            lblIn.Location = new Point(200, 22);
            lblIn.AutoSize = true;
            f.Controls.Add(lblIn);

            TextBox txt = new TextBox();
            txt.Name = "Input";
            txt.AccessibleName = "Input";
            txt.Location = new Point(200, 44);
            txt.Width = 220;
            txt.TextChanged += delegate
            {
                Log("\"event\":\"value_changed\",\"id\":\"Input\",\"value\":" + Escape(txt.Text));
            };
            f.Controls.Add(txt);

            // ── ComboBox：带子项。用来验证"树不该把 ComboBox 当叶节点剪掉" ──
            Label lblCmb = new Label();
            lblCmb.Text = "Choice";
            lblCmb.Location = new Point(200, 86);
            lblCmb.AutoSize = true;
            f.Controls.Add(lblCmb);

            ComboBox cmb = new ComboBox();
            cmb.Name = "Choice";
            cmb.AccessibleName = "Choice";
            cmb.Location = new Point(200, 108);
            cmb.Width = 220;
            cmb.DropDownStyle = ComboBoxStyle.DropDownList;
            cmb.Items.AddRange(new object[] { "A", "B", "C" });
            cmb.SelectedIndexChanged += delegate
            {
                Log("\"event\":\"selection\",\"id\":\"Choice\",\"value\":" + Escape(Convert.ToString(cmb.SelectedItem)));
            };
            cmb.SelectedIndex = 0;
            f.Controls.Add(cmb);

            // ── ListView：带列头。Header 曾经被当作叶节点剪掉。──
            ListView lv = new ListView();
            lv.Name = "Items";
            lv.AccessibleName = "Items";
            lv.Location = new Point(200, 150);
            lv.Width = 400;
            lv.Height = 160;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.Columns.Add("Key", 120);
            lv.Columns.Add("Value", 240);
            lv.Items.Add(new ListViewItem(new[] { "row-one", "first" }));
            lv.Items.Add(new ListViewItem(new[] { "row-two", "second" }));
            lv.SelectedIndexChanged += delegate
            {
                string sel = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Text : "";
                Log("\"event\":\"list_select\",\"id\":\"Items\",\"value\":" + Escape(sel));
            };
            f.Controls.Add(lv);

            // ── 接收区：记录滚轮与拖动 ──
            //
            // 滚轮和拖动没有"按钮"可点，所以需要一块自己的区域来接收事件。
            // 没有它，验证滚轮就只能靠"看页面滚了"，而那不可核对。
            Panel pad = new Panel();
            pad.Name = "Pad";
            pad.AccessibleName = "Pad";
            pad.BackColor = Color.FromArgb(245, 247, 250);
            pad.BorderStyle = BorderStyle.FixedSingle;
            pad.Location = new Point(20, 190);
            pad.Size = new Size(150, 220);
            f.Controls.Add(pad);

            Label padHint = new Label();
            padHint.Text = "滚轮 / 拖动区\n(把鼠标放在这里)";
            padHint.Dock = DockStyle.Fill;
            padHint.TextAlign = ContentAlignment.MiddleCenter;
            padHint.ForeColor = Color.Gray;
            // ★ 必须禁用，否则它会把鼠标事件全吃掉。
            //
            // 实测：把 Label 铺满 Pad 之后，中键点击、滚轮、拖动全部落在
            // Label 上，Pad 一个事件都收不到 —— 而 desktop_click 的
            // "该坐标下没有可交互元素（读到的是一层 Text）" 正是这个意思。
            // 那条警告替我指出了靶子本身的 bug。
            padHint.Enabled = false;
            pad.Controls.Add(padHint);

            int wheelSum = 0;
            pad.MouseWheel += delegate(object sender, MouseEventArgs e)
            {
                wheelSum += e.Delta;
                Log("\"event\":\"wheel\",\"id\":\"Pad\",\"delta\":" + e.Delta +
                    ",\"sum\":" + wheelSum);
            };
            pad.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                Log("\"event\":\"pad_down\",\"id\":\"Pad\",\"button\":\"" + e.Button + "\"");
            };
            pad.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                Log("\"event\":\"pad_up\",\"id\":\"Pad\",\"button\":\"" + e.Button + "\"");
            };
            pad.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                // 只在按下状态记录移动 —— 那是"拖动"，否则是普通悬停
                if (e.Button != MouseButtons.None)
                    Log("\"event\":\"pad_drag\",\"id\":\"Pad\",\"x\":" + e.X +
                        ",\"y\":" + e.Y + ",\"button\":\"" + e.Button + "\"");
            };

            // ── 状态栏：让人一眼看出"靶子在、日志在写" ──
            Label status = new Label();
            status.Name = "Status";
            status.AccessibleName = "Status";
            status.Text = "log: " + LogPath;
            status.Location = new Point(20, h - 40);
            status.AutoSize = false;
            status.Width = w - 40;
            status.ForeColor = Color.DimGray;
            f.Controls.Add(status);

            f.Shown += delegate
            {
                // 自己抢一次前台。
                //
                // helper 的注入类命令要求目标在前台（否则它会拒绝，或者在
                // 受限环境里激活失败）。测试靶存在的意义就是被操作，
                // 所以它主动把焦点拿回来是合理的；而且它启动之后很快就会
                // 失去焦点（用户在别的窗口里点一下就行），所以这个动作
                // 只在启动时做一次。
                try { f.Activate(); } catch { }

                // ready 事件里带上**每个控件的屏幕矩形**。
                //
                // 没有它，测试只能"点个大概位置然后看有没有事件"，那证明不了
                // 落点准不准 —— 恰好点中另一个按钮也会产生事件。带上矩形之后，
                // 测试能精确算出"Alpha 中心在哪"，再要求日志里出现的正是 Alpha。
                StringBuilder rects = new StringBuilder();
                rects.Append("[");
                bool firstRect = true;
                foreach (Control c in f.Controls)
                {
                    if (string.IsNullOrEmpty(c.AccessibleName)) continue;
                    if (c is Label) continue;               // 标签不参与点击验证
                    try
                    {
                        Point sp = c.PointToScreen(Point.Empty);
                        if (!firstRect) rects.Append(",");
                        firstRect = false;
                        rects.Append("{\"name\":").Append(Escape(c.AccessibleName))
                             .Append(",\"x\":").Append(sp.X)
                             .Append(",\"y\":").Append(sp.Y)
                             .Append(",\"w\":").Append(c.Width)
                             .Append(",\"h\":").Append(c.Height).Append("}");
                    }
                    catch { }
                }
                rects.Append("]");

                Log("\"event\":\"ready\",\"hwnd\":" + f.Handle.ToInt64() +
                    ",\"width\":" + f.ClientSize.Width +
                    ",\"height\":" + f.ClientSize.Height +
                    ",\"controls\":" + rects.ToString());
            };
            f.FormClosed += delegate
            {
                Log("\"event\":\"closed\"");
            };

            Application.Run(f);
            return 0;
        }

        private static void AddButton(Form f, string name, int x, int y, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = name;
            b.Name = name;
            b.AccessibleName = name;
            b.Location = new Point(x, y);
            b.Width = 150;
            b.Height = 32;
            b.Click += onClick;
            f.Controls.Add(b);
        }
    }
}
