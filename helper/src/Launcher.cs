using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

// 应用清单与启动。
//
// 为什么这个模块是必需的，而不是"顺手的便利功能"：
//
//   插件原来只能操作**已经打开的**窗口。而"把应用打开"这件事它做不到——
//   于是使用者只能从 shell 里启动，而 shell 不归插件的白名单管。
//   后果不是"少了个功能"，而是**授权顺序整个反了**：
//   应用先被打开（未经检查），然后才问"能不能操作它"。
//
//   正确顺序必须是 授权 → 启动 → 操作。launch_app 就是让这个顺序成立的那一环。
//
// ── 清单从哪来 ──
//
// 只取注册表里**声明了显示名**的已安装程序（卸载项里的 DisplayName），
// 加上 App Paths（那些是"能在运行对话框里直接敲名字启动"的程序）。
// 不去扫开始菜单快捷方式：那需要解析 .lnk，而且会把一堆卸载入口、
// 帮助文档也算成"应用"，清单会变得没法用。
//
// ── 启动方式 ──
//
// 用 Process.Start + UseShellExecute，让 shell 处理工作目录与关联。
// 但**路径必须来自清单或显式给出的 .exe**——不接受任意命令行，
// 否则这个接口就成了"以用户权限执行任意程序"，比不提供它还糟。
internal static class Launcher
{
    /// <summary>清单里的一个应用。</summary>
    internal sealed class AppEntry
    {
        public string Name = "";
        public string Exe = "";
        public string Version = "";
        public string Source = "";   // 来自卸载项还是 App Paths
    }

    private static List<AppEntry> _cache;
    private static int _diagScanned, _diagNoName, _diagSysComp, _diagParent, _diagNoExe, _diagDup;

    /// <summary>已安装应用的清单。第一次调用时枚举并缓存。</summary>
    internal static List<AppEntry> List()
    {
        if (_cache != null) return _cache;

        List<AppEntry> list = new List<AppEntry>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ① 卸载项：有 DisplayName 的才算"用户认得的应用"
        string[] uninstallRoots = new string[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        RegistryKey[] hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };

        foreach (RegistryKey hive in hives)
        {
            foreach (string root in uninstallRoots)
            {
                try
                {
                    using (RegistryKey k = hive.OpenSubKey(root))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey e = k.OpenSubKey(sub))
                                {
                                    if (e == null) continue;
                                    _diagScanned++;
                                    string name = e.GetValue("DisplayName") as string;
                                    if (string.IsNullOrEmpty(name)) { _diagNoName++; continue; }
                                    // 系统组件与更新包不是"应用"，会把清单淹掉
                                    if (e.GetValue("SystemComponent") is int &&
                                        (int)e.GetValue("SystemComponent") == 1) { _diagSysComp++; continue; }
                                    if (e.GetValue("ParentKeyName") != null) { _diagParent++; continue; }

                                    string exe = FindExe(e, sub);
                                    if (string.IsNullOrEmpty(exe)) { _diagNoExe++; continue; }
                                    if (!seen.Add(exe)) { _diagDup++; continue; }

                                    list.Add(new AppEntry
                                    {
                                        Name = name.Trim(),
                                        Exe = exe,
                                        Version = (e.GetValue("DisplayVersion") as string ?? "").Trim(),
                                        Source = "uninstall",
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        // ② App Paths：能在"运行"里直接敲名字启动的程序
        string[] appPathRoots = new string[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
        };
        foreach (RegistryKey hive in hives)
        {
            foreach (string root in appPathRoots)
            {
                try
                {
                    using (RegistryKey k = hive.OpenSubKey(root))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey e = k.OpenSubKey(sub))
                                {
                                    if (e == null) continue;
                                    string exe = (e.GetValue(null) as string ?? "").Trim().Trim('"');
                                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
                                    if (!seen.Add(exe)) continue;

                                    string baseName = Path.GetFileNameWithoutExtension(sub);
                                    list.Add(new AppEntry
                                    {
                                        Name = baseName,
                                        Exe = exe,
                                        Version = "",
                                        Source = "apppath",
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        list.Sort(delegate (AppEntry a, AppEntry b)
        {
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        _cache = list;
        return list;
    }

    /// <summary>
    /// 从卸载项里找可执行文件。
    ///
    /// 三种线索，按可靠性排序：
    ///   ① DisplayIcon  —— 最可靠，格式是 "C:\...\app.exe,0" 或 "C:\...\app.ico"
    ///   ② InstallLocation + 目录里唯一的 exe
    ///   ③ UninstallString 的目录
    /// 取不到就返回空——**宁可漏掉一个应用，也不要猜错一个路径**。
    /// 猜错的后果是启动到别的程序，而这是要授权的操作。
    /// </summary>
    private static string FindExe(RegistryKey e, string subKeyName)
    {
        // ① DisplayIcon
        try
        {
            string icon = e.GetValue("DisplayIcon") as string;
            if (!string.IsNullOrEmpty(icon))
            {
                string p = icon.Trim().Trim('"');
                int comma = p.LastIndexOf(',');
                if (comma > 2) p = p.Substring(0, comma);
                p = p.Trim().Trim('"');
                if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;
            }
        }
        catch { }

        // ② InstallLocation
        try
        {
            string dir = e.GetValue("InstallLocation") as string;
            if (!string.IsNullOrEmpty(dir))
            {
                dir = dir.Trim().Trim('"').TrimEnd('\\');
                if (Directory.Exists(dir))
                {
                    string hit = PickExe(dir);
                    if (!string.IsNullOrEmpty(hit)) return hit;
                }
            }
        }
        catch { }

        // ③ UninstallString 所在目录
        try
        {
            string us = e.GetValue("UninstallString") as string;
            if (!string.IsNullOrEmpty(us))
            {
                us = us.Trim().Trim('"');
                int sp = us.IndexOf(" -");
                if (sp > 0) us = us.Substring(0, sp).Trim().Trim('"');
                string dir = Path.GetDirectoryName(us);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string hit = PickExe(dir);
                    if (!string.IsNullOrEmpty(hit)) return hit;
                }
            }
        }
        catch { }

        return "";
    }

    /// <summary>
    /// 在一个目录里挑最像主程序的 exe。
    ///
    /// 规则刻意保守：优先与目录同名的，否则只有唯一一个 exe 时才取。
    /// 多个候选且没有同名时返回空——**清单里宁可少一个，也不要指向 helper
    /// 或卸载程序**。
    /// </summary>
    private static string PickExe(string dir)
    {
        try
        {
            string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
            if (exes.Length == 0) return "";

            string dirName = Path.GetFileName(dir.TrimEnd('\\'));
            foreach (string f in exes)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(f), dirName,
                                  StringComparison.OrdinalIgnoreCase))
                    return f;
            }

            // 过滤掉明显不是主程序的
            List<string> candidates = new List<string>();
            foreach (string f in exes)
            {
                string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (n.Contains("uninst") || n.Contains("setup") || n.Contains("update") ||
                    n.Contains("crash") || n.Contains("helper") || n.Contains("report"))
                    continue;
                candidates.Add(f);
            }
            if (candidates.Count == 1) return candidates[0];
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 按名字或路径解析到一个确定的可执行文件。
    ///
    /// 只接受"清单里有的"或"调用方明确给出的 .exe 完整路径"。
    /// **不接受任意命令行**——否则这个接口等同于以用户权限执行任意程序。
    /// </summary>
    internal static string Resolve(string query)
    {
        if (string.IsNullOrEmpty(query)) return "";

        string q = query.Trim();

        // ① 完整路径的 .exe（调用方明确指定）
        if (q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            (q.Contains("\\") || q.Contains("/")))
        {
            string full;
            try { full = Path.GetFullPath(q); } catch { return ""; }
            return File.Exists(full) ? full : "";
        }

        List<AppEntry> list = List();

        // ② 精确匹配显示名（不区分大小写）
        foreach (AppEntry a in list)
            if (string.Equals(a.Name, q, StringComparison.OrdinalIgnoreCase)) return a.Exe;

        // ③ 匹配 exe 文件名（含或不含扩展名）
        string qFile = q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? q : q + ".exe";
        foreach (AppEntry a in list)
            if (string.Equals(Path.GetFileName(a.Exe), qFile, StringComparison.OrdinalIgnoreCase))
                return a.Exe;

        // ④ 显示名包含（只在唯一命中时才认，避免"QQ"匹配到一堆东西）
        List<AppEntry> hits = new List<AppEntry>();
        foreach (AppEntry a in list)
            if (a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(a);
        if (hits.Count == 1) return hits[0].Exe;

        return "";
    }

    /// <summary>名字包含查询的所有候选，用于"没匹配上时给出提示"。</summary>
    internal static List<AppEntry> Search(string query, int max)
    {
        List<AppEntry> res = new List<AppEntry>();
        if (string.IsNullOrEmpty(query)) return res;
        foreach (AppEntry a in List())
        {
            if (a.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                Path.GetFileName(a.Exe).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                res.Add(a);
                if (res.Count >= max) break;
            }
        }
        return res;
    }

    /// <summary>启动。返回新进程的 pid，失败返回 0 并给出原因。</summary>
    internal static int Launch(string exe, out string error)
    {
        error = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.UseShellExecute = true;          // 让 shell 处理工作目录与关联
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process p = Process.Start(psi);
            if (p == null) { error = "Process.Start 返回 null"; return 0; }
            return p.Id;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return 0;
        }
    }
}
