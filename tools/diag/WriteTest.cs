using System;
using System.IO;

// 最小写文件测试：确认从本目录启动的 exe 到底能不能落盘。
//
// 为什么需要它：一个更复杂的程序"跑了但什么也没留"，可能是
// 窗体没起来、异常被吞、或者**根本写不了文件**。这三种的修法完全不同，
// 而症状一模一样（"什么都没生成"）。先用最小程序把变量压到只剩一个。
class WriteTest
{
    static int Main(string[] args)
    {
        string[] targets = new string[]
        {
            @"D:\wtest-1.txt",
            @"D:\dsh workspace\wtest-2.txt",
            @"D:\dsh workspace\dsh-desktop-control\wtest-3.txt",
            Path.Combine(Path.GetTempPath(), "wtest-4.txt"),
            @"C:\Users\a1838\AppData\Local\Temp\wtest-5.txt",
        };

        for (int i = 0; i < targets.Length; i++)
        {
            string p = targets[i];
            try
            {
                File.WriteAllText(p, "written at " + DateTime.Now.ToString("HH:mm:ss.fff") + "\r\n");
                Console.WriteLine("OK   " + p);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL " + p + "  -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        // 也试试追加模式与目录创建
        try
        {
            string dir = @"D:\wtest-dir";
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "inner.txt"), "appended\r\n");
            Console.WriteLine("OK   创建目录 + 追加: " + dir);
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL 创建目录  -> " + e.GetType().Name + ": " + e.Message);
        }

        return 0;
    }
}
