using System;
using System.Runtime.InteropServices;
using System.Text;

class WinstaCheck
{
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder buf, uint len, out uint needed);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetUserObjectInformationW(IntPtr h, int index, ref int buf, uint len, out uint needed);

    const int UOI_NAME = 2;
    const uint DESKTOP_READOBJECTS = 0x0001;
    const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    static string Name(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        StringBuilder sb = new StringBuilder(512);
        uint need;
        if (GetUserObjectInformationW(h, UOI_NAME, sb, (uint)sb.Capacity * 2, out need))
            return sb.ToString();
        return "(读取失败 err=" + Marshal.GetLastWin32Error() + ")";
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 窗口站 / 桌面归属检查 ===");
        Console.WriteLine("进程 pid = " + GetCurrentProcessId());
        Console.WriteLine("线程 tid = " + GetCurrentThreadId());
        Console.WriteLine();

        IntPtr ws = GetProcessWindowStation();
        IntPtr desk = GetThreadDesktop(GetCurrentThreadId());
        Console.WriteLine("本进程 GetProcessWindowStation() = " + ws + "  name = " + Name(ws));
        Console.WriteLine("本线程 GetThreadDesktop()       = " + desk + "  name = " + Name(desk));
        Console.WriteLine();

        // 交互式输入桌面
        IntPtr input = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
        if (input == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine("OpenInputDesktop() = 失败 (err=" + err + ": " +
                new System.ComponentModel.Win32Exception(err).Message + ")");
        }
        else
        {
            Console.WriteLine("OpenInputDesktop() = " + input + "  name = " + Name(input) +
                "   ← 当前交互式输入桌面");
            CloseDesktop(input);
        }

        Console.WriteLine();
        Console.WriteLine("判读：");
        Console.WriteLine("  进程窗口站应为 WinSta0；桌面应为 Default。");
        Console.WriteLine("  若两者与此不符，说明本进程不在交互式桌面上——");
        Console.WriteLine("  这正是低层键盘钩子收不到按键、GetAsyncKeyState 恒为 0 的原因。");
    }
}
