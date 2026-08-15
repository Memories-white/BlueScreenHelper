using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BlueScreenHelper.Models;
using Microsoft.Win32;

namespace BlueScreenHelper.Services;

public static class SystemScanner
{
    public static ScanReport ScanAll()
    {
        var report = new ScanReport
        {
            System = GetSystemSnapshot()
        };
        var issues = report.Issues;

        ScanBugChecks(issues);
        ScanUnexpectedShutdowns(issues);
        ScanWheaErrors(issues);
        ScanDiskErrors(issues);
        ScanMemoryDiagnostics(issues);
        ScanDumpFiles(issues);
        ScanDriverLoadFailures(issues);
        ScanSmartStatus(issues);
        ScanLogicalDisks(issues);
        ScanApplicationErrors(issues);
        ScanPendingReboot(issues);

        return report;
    }

    public static SystemSnapshot GetSystemSnapshot()
    {
        var snap = new SystemSnapshot
        {
            ComputerName = Environment.MachineName,
            Arch = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount
        };

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                snap.OSName = (key.GetValue("ProductName") as string) ?? "Windows";
                snap.Edition = (key.GetValue("EditionID") as string) ?? "";
                snap.Build = (key.GetValue("CurrentBuildNumber") as string) ?? "";
                if (int.TryParse(snap.Build, out var buildNum) && buildNum >= 22000)
                {
                    snap.OSName = snap.OSName.Replace("Windows 10", "Windows 11");
                }
                var ubr = key.GetValue("UBR");
                snap.OSVersion = $"{(key.GetValue("DisplayVersion") as string ?? "")} (Build {snap.Build}.{(ubr?.ToString() ?? "0")})";
            }
        }
        catch
        {
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                snap.Manufacturer = Convert.ToString(mo["Manufacturer"]) ?? "";
                snap.MachineModel = Convert.ToString(mo["Model"]) ?? "";
                break;
            }
        }
        catch
        {
        }

        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            snap.TotalMemory = $"{status.ullTotalPhys / 1024.0 / 1024.0 / 1024.0:F1} GB";
            snap.FreeMemory = $"{status.ullAvailPhys / 1024.0 / 1024.0 / 1024.0:F1} GB";
            snap.MemoryLoadPercent = $"{status.dwMemoryLoad}%";
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        snap.Uptime = FormatUptime(uptime);
        snap.LastBoot = DateTime.Now - uptime;

        return snap;
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1)
        {
            return $"{(int)t.TotalDays} 天 {t.Hours} 小时 {t.Minutes} 分钟";
        }
        if (t.TotalHours >= 1)
        {
            return $"{t.Hours} 小时 {t.Minutes} 分钟";
        }
        return $"{t.Minutes} 分钟";
    }

    private static void ScanBugChecks(List<ScanIssue> issues)
    {
        var events = QueryEvents("System",
            "*[System[(EventID=1001) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 20);

        var bugChecks = new List<(string Code, string Name, DateTime? Time, string? Driver)>();
        foreach (var e in events)
        {
            var desc = e.Description ?? "";
            if (!desc.Contains("0x", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var m = Regex.Match(desc, @"0x([0-9A-Fa-f]{8})");
            if (!m.Success)
            {
                continue;
            }
            var code = Convert.ToUInt32(m.Groups[1].Value, 16);
            var entry = BugCheckDatabase.Get(code);
            var driverMatch = Regex.Match(desc, @"([a-zA-Z0-9_\-]+\.sys)", RegexOptions.IgnoreCase);
            bugChecks.Add((entry?.CodeHex ?? $"0x{code:X8}", entry?.Name ?? "未知错误", e.Time, driverMatch.Success ? driverMatch.Groups[1].Value : null));
        }

        if (bugChecks.Count == 0)
        {
            issues.Add(new ScanIssue
            {
                Category = "崩溃记录",
                Title = "近 30 天无蓝屏崩溃记录",
                Severity = IssueSeverity.Info,
                Description = "系统日志中未发现 BugCheck (Event ID 1001) 记录，系统运行正常。",
                Recommendation = "无需处理。"
            });
            return;
        }

        foreach (var (code, name, time, driver) in bugChecks.Take(5))
        {
            var entry = BugCheckDatabase.Get(Convert.ToUInt32(code.Substring(2), 16));
            issues.Add(new ScanIssue
            {
                Category = "崩溃记录",
                Title = $"检测到蓝屏：{code} {name}",
                Severity = IssueSeverity.Critical,
                Description = $"崩溃时间：{time:yyyy-MM-dd HH:mm:ss}{(driver != null ? $"\n转储报告中涉及的驱动：{driver}" : "")}\n{entry?.Description ?? "知识库未收录该错误代码。"}",
                Recommendation = string.Join("\n", entry?.Solutions ?? new[] { "使用转储分析功能解析 .dmp 文件，或使用 AI 诊断获取详细方案。" })
            });
        }

        var distinct = bugChecks.Select(b => b.Code).Distinct().Count();
        issues.Add(new ScanIssue
        {
            Category = "崩溃记录",
            Title = $"近 30 天共发生 {bugChecks.Count} 次蓝屏（{distinct} 种错误代码）",
            Severity = bugChecks.Count >= 3 ? IssueSeverity.Warning : IssueSeverity.Info,
            Description = "频繁蓝屏通常指向驱动、内存或硬件问题。",
            Recommendation = "建议逐个分析 Minidump 目录中的转储文件，找出共性驱动；同时运行内存诊断。"
        });
    }

    private static void ScanUnexpectedShutdowns(List<ScanIssue> issues)
    {
        var events = QueryEvents("System",
            "*[System[(EventID=6008) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 5);
        if (events.Count == 0)
        {
            return;
        }
        var latest = events[0];
        issues.Add(new ScanIssue
        {
            Category = "崩溃记录",
            Title = $"检测到 {events.Count} 次异常关机",
            Severity = IssueSeverity.Warning,
            Description = $"最近一次异常关机发生在 {latest.Time:yyyy-MM-dd HH:mm:ss}，可能由断电、硬件故障或系统崩溃引起。",
            Recommendation = "检查电源连接与供电稳定性；异常关机常与蓝屏（Kernel-Power 41）同时出现，可结合转储分析定位。"
        });
    }

    private static void ScanWheaErrors(List<ScanIssue> issues)
    {
        var events = QueryEvents("System",
            "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=46 or EventID=47 or EventID=1) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 5);
        if (events.Count == 0)
        {
            return;
        }
        var idText = string.Join(",", events.Select(e => e.Id.ToString()).Distinct());
        issues.Add(new ScanIssue
        {
            Category = "硬件",
            Title = $"检测到 {events.Count} 条 WHEA 硬件错误 (Event ID {idText})",
            Severity = IssueSeverity.Critical,
            Description = "Windows 硬件错误架构记录了无法纠正的硬件错误，通常与 CPU、内存、主板或电源相关。\n" +
                          $"最近一次：{events[0].Time:yyyy-MM-dd HH:mm:ss}",
            Recommendation = "1) 恢复 BIOS 默认设置（尤其关闭内存 XMP/EXPO 超频）\n2) 运行内存诊断 mdsched.exe\n3) 检查散热与供电\n4) 更新 BIOS"
        });
    }

    private static void ScanDiskErrors(List<ScanIssue> issues)
    {
        var events = QueryEvents("System",
            "*[System[(EventID=7 or EventID=11 or EventID=51 or EventID=153 or EventID=55) and (Provider[@Name='disk'] or Provider[@Name='Ntfs']) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 5);
        if (events.Count == 0)
        {
            return;
        }
        var idText = string.Join(",", events.Select(e => e.Id.ToString()).Distinct());
        issues.Add(new ScanIssue
        {
            Category = "存储",
            Title = $"检测到 {events.Count} 条磁盘/文件系统错误 (Event ID {idText})",
            Severity = IssueSeverity.Critical,
            Description = $"最近一次磁盘错误：{events[0].Time:yyyy-MM-dd HH:mm:ss}\n磁盘错误可导致蓝屏、文件损坏与数据丢失。",
            Recommendation = "1) 立即备份重要数据\n2) 运行 chkdsk C: /f /r 检查系统盘\n3) 使用 CrystalDiskInfo 检查 SMART 状态\n4) 更换 SATA/电源线，必要时更换磁盘"
        });
    }

    private static void ScanMemoryDiagnostics(List<ScanIssue> issues)
    {
        var events = QueryEvents("System",
            "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1101 or EventID=1102 or EventID=1201 or EventID=1202) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 5);
        if (events.Count == 0)
        {
            issues.Add(new ScanIssue
            {
                Category = "内存",
                Title = "近 30 天未运行内存诊断",
                Severity = IssueSeverity.Info,
                Description = "Windows 内存诊断 (mdsched.exe) 可检查物理内存故障，是蓝屏排查的重要工具。",
                Recommendation = "运行 mdsched.exe 并选择“立即重启检查”，重启后查看诊断结果。"
            });
            return;
        }
        var hasError = events.Any(e => e.Id == 1201 || e.Id == 1202);
        issues.Add(new ScanIssue
        {
            Category = "内存",
            Title = hasError ? "内存诊断发现硬件错误" : "内存诊断通过",
            Severity = hasError ? IssueSeverity.Critical : IssueSeverity.Info,
            Description = $"最近一次诊断时间：{events[0].Time:yyyy-MM-dd HH:mm:ss}",
            Recommendation = hasError
                ? "1) 逐条测试内存，找出故障条\n2) 清洁内存金手指与插槽\n3) 必要时更换内存"
                : "内存健康，无需处理。"
        });
    }

    private static void ScanDumpFiles(List<ScanIssue> issues)
    {
        var files = new List<(string Path, DateTime Time, long Size)>();
        try
        {
            var minidump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (Directory.Exists(minidump))
            {
                foreach (var f in Directory.GetFiles(minidump, "*.dmp"))
                {
                    var fi = new FileInfo(f);
                    files.Add((f, fi.LastWriteTime, fi.Length));
                }
            }
            var memDump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
            if (File.Exists(memDump))
            {
                var fi = new FileInfo(memDump);
                files.Add((memDump, fi.LastWriteTime, fi.Length));
            }
        }
        catch
        {
        }

        files = files.OrderByDescending(f => f.Time).ToList();
        if (files.Count == 0)
        {
            issues.Add(new ScanIssue
            {
                Category = "转储文件",
                Title = "未找到崩溃转储文件",
                Severity = IssueSeverity.Info,
                Description = "C:\\Windows\\Minidump 目录中没有小转储文件，且不存在 MEMORY.DMP。\n若系统从未蓝屏，这是正常的。",
                Recommendation = "若希望保留崩溃现场，可在 系统属性 → 高级 → 启动和故障恢复 中确认“自动重新启动”与“核心内存转储”已启用。"
            });
            return;
        }

        issues.Add(new ScanIssue
        {
            Category = "转储文件",
            Title = $"找到 {files.Count} 个崩溃转储文件",
            Severity = files.Count >= 3 ? IssueSeverity.Warning : IssueSeverity.Info,
            Description = string.Join("\n", files.Take(5).Select(f =>
                $"{(Path.GetFileName(f.Path) == "MEMORY.DMP" ? "完整转储" : "小转储")} {Path.GetFileName(f.Path)}：{f.Time:yyyy-MM-dd HH:mm}（{DumpParser.FormatSize(f.Size)}）")),
            Recommendation = "使用本软件的“转储分析”功能逐个解析小转储文件，可快速定位故障驱动。"
        });
    }

    private static void ScanDriverLoadFailures(List<ScanIssue> issues)
    {
        var events = QueryEvents("System",
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=219 or EventID=410) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 10);
        if (events.Count == 0)
        {
            return;
        }
        var desc = events[0].Description ?? "";
        var devMatch = Regex.Match(desc, @"(\S+\s+\S+)\s+加载失败", RegexOptions.IgnoreCase);
        issues.Add(new ScanIssue
        {
            Category = "驱动程序",
            Title = $"检测到 {events.Count} 次驱动程序加载失败 (Event ID 219)",
            Severity = IssueSeverity.Warning,
            Description = $"最近一次：{events[0].Time:yyyy-MM-dd HH:mm:ss}\n{desc}",
            Recommendation = "在设备管理器中查找带黄色感叹号的设备，更新或重装其驱动；若为旧设备，可卸载后重新扫描硬件。"
        });
    }

    private static void ScanSmartStatus(List<ScanIssue> issues)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus"));
            var failed = new List<string>();
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                if (mo["PredictFailure"] is bool predict && predict)
                {
                    var name = Convert.ToString(mo["InstanceName"]) ?? "未知磁盘";
                    failed.Add(name);
                }
            }
            if (failed.Count > 0)
            {
                issues.Add(new ScanIssue
                {
                    Category = "存储",
                    Title = "SMART 预测磁盘即将发生故障",
                    Severity = IssueSeverity.Critical,
                    Description = "以下磁盘的 SMART 自检已标记预测性故障：\n" + string.Join("\n", failed),
                    Recommendation = "立即备份该磁盘数据，并使用官方工具（如 CrystalDiskInfo、厂商诊断工具）确认，及时更换磁盘。"
                });
            }
        }
        catch
        {
            issues.Add(new ScanIssue
            {
                Category = "存储",
                Title = "无法读取 SMART 磁盘健康状态",
                Severity = IssueSeverity.Info,
                Description = "读取 SMART 数据失败，可能需要管理员权限或该磁盘不支持 S.M.A.R.T.。",
                Recommendation = "可尝试以管理员身份运行本程序，或使用 CrystalDiskInfo 检查磁盘健康。"
            });
        }
    }

    private static void ScanLogicalDisks(List<ScanIssue> issues)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDisk WHERE DriveType=3");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var id = Convert.ToString(mo["DeviceID"]) ?? "";
                var total = Convert.ToUInt64(mo["Size"] ?? 0UL);
                var free = Convert.ToUInt64(mo["FreeSpace"] ?? 0UL);
                if (total == 0)
                {
                    continue;
                }
                var usedPercent = (double)(total - free) / total * 100.0;
                if (usedPercent >= 90)
                {
                    issues.Add(new ScanIssue
                    {
                        Category = "存储",
                        Title = $"磁盘 {id} 空间不足",
                        Severity = IssueSeverity.Warning,
                        Description = $"已用空间 {usedPercent:F1}%（剩余 {free / 1073741824.0:F1} GB / 共 {total / 1073741824.0:F1} GB）。",
                        Recommendation = "清理临时文件与大型文件；空间不足会影响系统稳定性与页面文件。"
                    });
                }
            }
        }
        catch
        {
        }
    }

    private static void ScanApplicationErrors(List<ScanIssue> issues)
    {
        var events = QueryEvents("Application",
            "*[System[(EventID=1000 or EventID=1001 or EventID=1002 or EventID=1003 or EventID=1026) and Level<=3 and TimeCreated[timediff(@SystemTime) <= 604800000]]]", 30);

        var groups = events
            .Select(e => new
            {
                App = ExtractAppName(e.Description ?? ""),
                e.Time,
                e.Id
            })
            .Where(x => !string.IsNullOrEmpty(x.App))
            .GroupBy(x => x.App)
            .Select(g => new { App = g.Key, Count = g.Count(), Latest = g.Max(x => x.Time) })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToList();

        if (groups.Count == 0)
        {
            return;
        }
        var summary = string.Join("\n", groups.Select(g => $"{g.App}：{g.Count} 次（最近 {g.Latest:MM-dd HH:mm}）"));
        issues.Add(new ScanIssue
        {
            Category = "应用程序",
            Title = $"近 7 天有 {groups.Sum(g => g.Count)} 次应用程序崩溃",
            Severity = IssueSeverity.Info,
            Description = "以下程序出现崩溃：\n" + summary,
            Recommendation = "更新对应软件至最新版本；崩溃频繁的程序可尝试重装。应用程序崩溃一般不会导致蓝屏，但可能是内存/驱动异常的信号。"
        });
    }

    private static void ScanPendingReboot(List<ScanIssue> issues)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (key != null || Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null)
            {
                issues.Add(new ScanIssue
                {
                    Category = "系统",
                    Title = "系统更新需要重启",
                    Severity = IssueSeverity.Info,
                    Description = "Windows 更新已安装但尚未重启生效。",
                    Recommendation = "请及时重启电脑，未完成更新的系统可能影响稳定性。"
                });
            }
        }
        catch
        {
        }
    }

    private static string ExtractAppName(string description)
    {
        var m = Regex.Match(description, @"应用程序名称:\s*([^\s,]+\.exe)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }
        m = Regex.Match(description, @"faulting application name:\s*([^\s,]+\.exe)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<EventInfo> QueryEvents(string logName, string xpath, int max)
    {
        var result = new List<EventInfo>();
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var seen = 0;
            while (seen < max)
            {
                using var record = reader.ReadEvent();
                if (record == null)
                {
                    break;
                }
                seen++;
                string? desc = null;
                try
                {
                    desc = record.FormatDescription();
                }
                catch
                {
                }
                result.Add(new EventInfo
                {
                    Id = record.Id,
                    Time = record.TimeCreated,
                    Level = SafeLevel(record.Level),
                    Description = desc
                });
            }
        }
        catch (Exception ex)
        {
            if (result.Count == 0)
            {
                result.Add(new EventInfo { Id = 0, Time = null, Level = "Error", Description = $"读取事件日志失败：{ex.Message}" });
            }
        }
        return result;
    }

    private static string SafeLevel(byte? level)
    {
        return level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            _ => level?.ToString() ?? ""
        };
    }

    private sealed class EventInfo
    {
        public int Id { get; set; }
        public DateTime? Time { get; set; }
        public string Level { get; set; } = "";
        public string? Description { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}