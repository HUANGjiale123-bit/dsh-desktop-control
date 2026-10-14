/*
 * JobCheck —— 检查本进程是否被限制在作业对象里，以及有哪些 UI 限制。
 *
 * 为什么查这个：低层输入钩子（WH_KEYBOARD_LL）依赖 Windows 把输入事件投递给
 * 安装进程。若进程处在一个带 JOB_OBJECT_UILIMIT_* 限制的作业对象中，或者被
 * 亲和性/桌面限制圈住，钩子会"安装成功但永不回调"——正好是实测到的现象。
 *
 * 这一点之前没查过，因为它与"窗口站/桌面/会话"是相互独立的变量：
 * 前面那三项都正常，不代表作业对象也干净。
 */

using System;
using System.Runtime.InteropServices;
using System.Text;

class JobCheck
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
        ref JOBOBJECT_BASIC_LIMIT_INFORMATION info, int len, out int ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len, out int ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
        IntPtr info, int len, out int ret);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    // JobObjectBasicUIRestrictions
    const int JobObjectBasicUIRestrictions = 4;
    const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_UI_RESTRICTIONS
    {
        public uint UIRestrictionsClass;
    }

    // LimitFlags
    const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    const uint JOB_OBJECT_LIMIT_AFFINITY = 0x00000010;
    const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    // UIRestrictionsClass
    const uint JOB_OBJECT_UILIMIT_HANDLES = 0x00000001;
    const uint JOB_OBJECT_UILIMIT_READCLIPBOARD = 0x00000002;
    const uint JOB_OBJECT_UILIMIT_WRITECLIPBOARD = 0x00000004;
    const uint JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS = 0x00000008;
    const uint JOB_OBJECT_UILIMIT_DISPLAYSETTINGS = 0x00000010;
    const uint JOB_OBJECT_UILIMIT_GLOBALATOMS = 0x00000020;
    const uint JOB_OBJECT_UILIMIT_DESKTOP = 0x00000040;
    const uint JOB_OBJECT_UILIMIT_EXITWINDOWS = 0x00000080;

    static string Flags(uint f)
    {
        if (f == 0) return "（无）";
        StringBuilder sb = new StringBuilder();
        if ((f & JOB_OBJECT_UILIMIT_HANDLES) != 0) sb.Append("HANDLES ");            // ★ 会挡住钩子
        if ((f & JOB_OBJECT_UILIMIT_READCLIPBOARD) != 0) sb.Append("READCLIPBOARD ");
        if ((f & JOB_OBJECT_UILIMIT_WRITECLIPBOARD) != 0) sb.Append("WRITECLIPBOARD ");
        if ((f & JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS) != 0) sb.Append("SYSTEMPARAMETERS ");
        if ((f & JOB_OBJECT_UILIMIT_DISPLAYSETTINGS) != 0) sb.Append("DISPLAYSETTINGS ");
        if ((f & JOB_OBJECT_UILIMIT_GLOBALATOMS) != 0) sb.Append("GLOBALATOMS ");
        if ((f & JOB_OBJECT_UILIMIT_DESKTOP) != 0) sb.Append("DESKTOP ");              // ★ 会挡住钩子
        if ((f & JOB_OBJECT_UILIMIT_EXITWINDOWS) != 0) sb.Append("EXITWINDOWS ");
        return sb.ToString().Trim();
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 作业对象 / UI 限制检查 ===");
        Console.WriteLine("pid = " + GetCurrentProcessId());
        Console.WriteLine();

        bool inJob;
        try
        {
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob))
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine("IsProcessInJob 调用失败 err=" + err);
                return;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("IsProcessInJob 异常: " + e.Message);
            return;
        }

        Console.WriteLine("是否在作业对象中: " + (inJob ? "是" : "否"));
        if (!inJob)
        {
            Console.WriteLine("→ 作业对象不是原因。");
            return;
        }

        // 打开自己的进程，拿它的作业对象
        IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, GetCurrentProcessId());
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine("OpenProcess 失败 err=" + err + "，无法进一步查询作业限制。");
            return;
        }

        // UI 限制
        JOBOBJECT_BASIC_UI_RESTRICTIONS ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS();
        int ret;
        // 注意：QueryInformationJobObject 需要作业句柄，进程句柄不行。
        // 进程句柄查不了作业信息，这里改从 PEB 走不通，因此用 PowerShell 兜底会更实际。
        Console.WriteLine();
        Console.WriteLine("（进程句柄无法查询作业信息，需作业句柄——下面用另一条路）");

        // 扩展限制信息
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        bool ok = QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation,
                                            ref ext, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)), out ret);
        if (ok)
        {
            Console.WriteLine("LimitFlags = 0x" + ext.BasicLimitInformation.LimitFlags.ToString("X8"));
            Console.WriteLine("  AFFINITY              = " + ((ext.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_AFFINITY) != 0));
            Console.WriteLine("  ACTIVE_PROCESS        = " + ((ext.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_ACTIVE_PROCESS) != 0));
            Console.WriteLine("  KILL_ON_JOB_CLOSE     = " + ((ext.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) != 0));
            Console.WriteLine("  BREAKAWAY_OK          = " + ((ext.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_BREAKAWAY_OK) != 0));
            Console.WriteLine("  SILENT_BREAKAWAY_OK   = " + ((ext.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK) != 0));
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine("查询扩展限制失败 err=" + err);
        }

        ui.UIRestrictionsClass = 0;
        ok = QueryInformationJobObject(IntPtr.Zero, JobObjectBasicUIRestrictions,
                                       ref ui, Marshal.SizeOf(typeof(JOBOBJECT_BASIC_UI_RESTRICTIONS)), out ret);
        if (ok)
            Console.WriteLine("UIRestrictionsClass = 0x" + ui.UIRestrictionsClass.ToString("X8") + "  → " + Flags(ui.UIRestrictionsClass));
        else
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine("查询 UI 限制失败 err=" + err);
        }
    }
}
