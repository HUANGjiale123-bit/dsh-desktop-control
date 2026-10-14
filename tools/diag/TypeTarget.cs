using System;
using System.Windows.Forms;
using System.Drawing;

// 极简输入框窗口 —— 用来验证"此刻输入注入是否可用"。
//
// 用途：当用户（或另一个 agent）正在用真实鼠标键盘操作这台机器时，
// 让他点进这个框并打字。如果字进去了，就证明"输入注入在当前状态是通的"，
// 于是问题就收窄成"什么条件下不通"，而不是"从来不通"。
class TypeTarget
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Form f = new Form();
        f.Text = "请点进来打字";
        f.ClientSize = new Size(560, 150);
        f.StartPosition = FormStartPosition.CenterScreen;

        TextBox t = new TextBox();
        t.Name = "box";
        t.Multiline = true;
        t.Font = new Font("Consolas", 14);
        t.Text = "";
        t.SetBounds(12, 12, 530, 120);

        f.Controls.Add(t);
        Application.Run(f);
    }
}
