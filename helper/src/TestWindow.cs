using System;
using System.Windows.Forms;
using System.Drawing;

class TestWindow
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Form f = new Form();
        f.Text = "dsc-verify";
        f.ClientSize = new Size(520, 300);
        f.StartPosition = FormStartPosition.CenterScreen;

        // 一个单行输入框（有 ValuePattern）
        TextBox t1 = new TextBox();
        t1.Name = "editA";
        t1.Text = "AAA";
        t1.SetBounds(16, 16, 470, 30);

        // 一个按钮（可点击验证）
        Button b1 = new Button();
        b1.Name = "btnA";
        b1.Text = "点我";
        b1.SetBounds(16, 60, 120, 40);
        int clicks = 0;
        b1.Click += delegate { clicks++; b1.Text = "点了 " + clicks + " 次"; };

        // 一个多行框（只暴露 Document）
        TextBox t2 = new TextBox();
        t2.Name = "editMulti";
        t2.Multiline = true;
        t2.Text = "多行初始";
        t2.SetBounds(16, 115, 470, 100);

        // 一个复选框（可点）
        CheckBox c1 = new CheckBox();
        c1.Name = "chkA";
        c1.Text = "可勾选";
        c1.SetBounds(16, 228, 200, 30);

        // 一个数字框（RangeValuePattern）
        NumericUpDown n1 = new NumericUpDown();
        n1.Name = "numA";
        n1.Minimum = 0; n1.Maximum = 100; n1.Value = 50;
        n1.SetBounds(240, 228, 120, 30);

        f.Controls.Add(t1);
        f.Controls.Add(b1);
        f.Controls.Add(t2);
        f.Controls.Add(c1);
        f.Controls.Add(n1);

        Application.Run(f);
    }
}
