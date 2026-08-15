using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using BlueScreenHelper.Models;
using Microsoft.Win32;

namespace BlueScreenHelper.Services;

public sealed class AiToolDef
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Action { get; init; }
    public required string Description { get; init; }
    public required Func<string> Execute { get; init; }
}

public static class AiSystemTools
{
    public static IReadOnlyList<AiToolDef> Tools { get; } = new[]
    {
        new AiToolDef
        {
            Key = "system",
            Name = "系统基本信息",
            Action = "采集了系统版本、硬件配置与运行状态",
            Description = "系统版本、硬件配置、内存与运行时长",
            Execute = GetSystemInfo
        },
        new AiToolDef
        {
            Key = "bsod",
            Name = "蓝屏记录",
            Action = "扫描了近 30 天的蓝屏记录与崩溃转储文件",
            Description = "近30天蓝屏事件列表（错误代码、时间、涉及驱动）及转储文件清单",
            Execute = GetBsodRecords
        },
        new AiToolDef
        {
            Key = "dump",
            Name = "崩溃转储分析",
            Action = "解析了最新的崩溃转储文件（.dmp）",
            Description = "解析最新转储文件：错误代码、参数、疑似故障模块",
            Execute = GetDumpAnalysis
        },
        new AiToolDef
        {
            Key = "shutdown",
            Name = "异常关机记录",
            Action = "扫描了近 30 天的异常关机记录（Event 6008）",
            Description = "近30天异常关机事件列表",
            Execute = () => QueryEventsText("System",
                "*[System[(EventID=6008) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 10,
                "异常关机记录（Event ID 6008）", "未发现异常关机记录。")
        },
        new AiToolDef
        {
            Key = "whea",
            Name = "WHEA 硬件错误",
            Action = "扫描了近 30 天的 WHEA 硬件错误记录",
            Description = "近30天 WHEA-Logger 硬件错误（Event 17/18/19/46/47/1）",
            Execute = () => QueryEventsText("System",
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=46 or EventID=47 or EventID=1) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 10,
                "WHEA 硬件错误记录", "近 30 天未发现 WHEA 硬件错误。")
        },
        new AiToolDef
        {
            Key = "disk_errors",
            Name = "磁盘错误记录",
            Action = "扫描了近 30 天的磁盘与文件系统错误",
            Description = "近30天磁盘/文件系统错误（Event 7/11/51/55/153）",
            Execute = () => QueryEventsText("System",
                "*[System[(EventID=7 or EventID=11 or EventID=51 or EventID=153 or EventID=55) and (Provider[@Name='disk'] or Provider[@Name='Ntfs']) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 10,
                "磁盘/文件系统错误记录", "近 30 天未发现磁盘或文件系统错误。")
        },
        new AiToolDef
        {
            Key = "disk_space",
            Name = "磁盘空间",
            Action = "读取了各分区的空间使用情况",
            Description = "所有本地分区的总容量、剩余空间与使用率",
            Execute = GetDiskSpace
        },
        new AiToolDef
        {
            Key = "smart",
            Name = "SMART 磁盘健康",
            Action = "检查了磁盘 SMART 健康状态",
            Description = "磁盘 SMART 预测性故障状态",
            Execute = GetSmartStatus
        },
        new AiToolDef
        {
            Key = "memory",
            Name = "内存诊断结果",
            Action = "查询了近 30 天的内存诊断结果",
            Description = "近30天 Windows 内存诊断结果（Event 1101/1102/1201/1202）",
            Execute = () => QueryEventsText("System",
                "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1101 or EventID=1102 or EventID=1201 or EventID=1202) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 5,
                "内存诊断结果", "近 30 天未运行 Windows 内存诊断（mdsched.exe）。")
        },
        new AiToolDef
        {
            Key = "driver",
            Name = "驱动加载失败",
            Action = "扫描了近 30 天的驱动加载失败记录",
            Description = "近30天驱动加载失败（Kernel-PnP Event 219/410）",
            Execute = () => QueryEventsText("System",
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=219 or EventID=410) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 10,
                "驱动加载失败记录", "近 30 天未发现驱动加载失败。")
        },
        new AiToolDef
        {
            Key = "app_crashes",
            Name = "应用程序崩溃",
            Action = "扫描了近 7 天的应用程序崩溃记录",
            Description = "近7天应用程序崩溃统计（按程序分组）",
            Execute = GetAppCrashes
        },
        new AiToolDef
        {
            Key = "pending_reboot",
            Name = "待重启更新",
            Action = "检查了是否有待重启生效的更新",
            Description = "Windows 更新是否已安装但等待重启",
            Execute = GetPendingReboot
        }
    };

    public static AiToolDef? Find(string key)
    {
        return Tools.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public static string Instructions
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("[可用系统工具]");
            sb.AppendLine("本软件已内置系统数据采集能力。当你的分析需要本机真实数据（蓝屏记录、事件日志、磁盘状态等）时，必须按以下规则请求工具：");
            sb.AppendLine("- 在回复中单独输出一行 [[TOOL:工具键]]，该行不得包含其他任何内容；");
            sb.AppendLine("- 工具执行后，结果会以[系统采集结果]开头自动追加到对话中，请基于返回的真实数据继续分析；");
            sb.AppendLine("- 严禁编造或猜测系统数据；严禁建议用户手动打开事件查看器、安装调试工具或自行导出日志（本软件已内置全部采集能力）；");
            sb.AppendLine("- 可以依次请求多个工具，拿到全部所需结果后再输出最终分析；不要重复请求同一工具。");
            sb.AppendLine("可用工具：");
            foreach (var t in Tools)
            {
                sb.AppendLine($"- [[TOOL:{t.Key}]]：{t.Description}");
            }
            return sb.ToString();
        }
    }

    // ---------- 工具实现 ----------

    private static string GetSystemInfo()
    {
        var s = SystemScanner.GetSystemSnapshot();
        var sb = new StringBuilder();
        sb.AppendLine($"计算机名：{s.ComputerName}");
        sb.AppendLine($"操作系统：{s.OSName} {s.OSVersion}（版本 {s.Edition}，{s.Arch}）");
        sb.AppendLine($"硬件：{s.Manufacturer} {s.MachineModel}");
        sb.AppendLine($"逻辑处理器：{s.ProcessorCount} 个");
        sb.AppendLine($"内存：共 {s.TotalMemory}，可用 {s.FreeMemory}（负载 {s.MemoryLoadPercent}）");
        sb.AppendLine($"上次启动：{s.LastBoot:yyyy-MM-dd HH:mm:ss}，已运行 {s.Uptime}");
        return sb.ToString();
    }

    private static string GetBsodRecords()
    {
        var events = SystemScanner.QueryEvents("System",
            "*[System[(EventID=1001) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", 20);

        var sb = new StringBuilder();
        var count = 0;
        foreach (var e in events)
        {
            var desc = e.Description ?? "";
            var m = Regex.Match(desc, @"0x([0-9A-Fa-f]{8})");
            if (!m.Success)
            {
                continue;
            }
            count++;
            var code = Convert.ToUInt32(m.Groups[1].Value, 16);
            var entry = BugCheckDatabase.Get(code);
            var dm = Regex.Match(desc, @"([a-zA-Z0-9_\-]+\.sys)", RegexOptions.IgnoreCase);
            sb.AppendLine($"{e.Time:yyyy-MM-dd HH:mm}  {entry?.CodeHex ?? $"0x{code:X8}"} {entry?.Name ?? "未知错误"}" +
                          (dm.Success ? $"（涉及驱动 {dm.Groups[1].Value}）" : ""));
        }

        var dumpLine = "";
        var (dumpCount, latestDump) = GetDumpFileSummary();
        if (dumpCount > 0)
        {
            dumpLine = $"转储文件：共 {dumpCount} 个{(latestDump != null ? $"，最新 {Path.GetFileName(latestDump)} 于 {File.GetLastWriteTime(latestDump):yyyy-MM-dd HH:mm}" : "")}";
        }
        else
        {
            dumpLine = "转储文件：未找到（C:\\Windows\\Minidump 为空）";
        }

        return count == 0
            ? $"近 30 天未发现蓝屏（BugCheck）记录。\n{dumpLine}"
            : $"近 30 天共 {count} 次蓝屏（按时间倒序）：\n{sb}{dumpLine}";
    }

    private static (int Count, string? LatestPath) GetDumpFileSummary()
    {
        var files = new List<string>();
        try
        {
            var minidump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (Directory.Exists(minidump))
            {
                files.AddRange(Directory.GetFiles(minidump, "*.dmp"));
            }
            var memDump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
            if (File.Exists(memDump))
            {
                files.Add(memDump);
            }
        }
        catch
        {
        }
        if (files.Count == 0)
        {
            return (0, null);
        }
        var latest = files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
        return (files.Count, latest);
    }

    private static string GetDumpAnalysis()
    {
        var (count, latestPath) = GetDumpFileSummary();
        if (latestPath == null)
        {
            return "未找到崩溃转储文件（C:\\Windows\\Minidump 目录为空）。无法进行转储分析。";
        }
        var r = DumpParser.Parse(latestPath);
        if (!string.IsNullOrEmpty(r.Error))
        {
            return $"解析转储文件失败（{Path.GetFileName(latestPath)}）：{r.Error}";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"文件：{latestPath}（共 {count} 个转储文件，分析最新一个）");
        sb.AppendLine($"类型：{r.DumpTypeText}");
        if (r.DumpTime != null)
        {
            sb.AppendLine($"崩溃时间：{r.DumpTime:yyyy-MM-dd HH:mm:ss}");
        }
        sb.AppendLine($"错误代码：{r.BugCheckHex} {r.BugCheckName}");
        sb.AppendLine($"参数：{string.Join(" ", r.BugCheckParameters.Select(p => $"0x{p:X16}"))}");
        if (!string.IsNullOrEmpty(r.FaultingModule))
        {
            sb.AppendLine($"疑似故障模块：{r.FaultingModule}");
        }
        if (r.Knowledge != null)
        {
            sb.AppendLine($"知识库说明：{r.Knowledge.Description}");
            sb.AppendLine("建议方案：");
            foreach (var s in r.Knowledge.Solutions)
            {
                sb.AppendLine($"- {s}");
            }
        }
        if (r.Modules.Count > 0)
        {
            sb.AppendLine("转储中的主要模块（前 15 个）：");
            foreach (var mod in r.Modules.Take(15))
            {
                sb.AppendLine($"  {mod.Name}");
            }
        }
        return sb.ToString();
    }

    private static string QueryEventsText(string log, string xpath, int max, string title, string emptyText)
    {
        var events = SystemScanner.QueryEvents(log, xpath, max);
        if (events.Count == 0)
        {
            return emptyText;
        }
        return SystemScanner.BuildEventDetail(events, title, "");
    }

    private static string GetDiskSpace()
    {
        var sb = new StringBuilder();
        var count = 0;
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
                count++;
                var usedPercent = (double)(total - free) / total * 100.0;
                sb.AppendLine($"{id}：已用 {usedPercent:F1}%，剩余 {free / 1073741824.0:F1} GB / 共 {total / 1073741824.0:F1} GB");
            }
        }
        catch
        {
        }
        return count == 0 ? "无法读取磁盘分区信息。" : sb.ToString().TrimEnd();
    }

    private static string GetSmartStatus()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus"));
            var failed = new List<string>();
            var healthy = 0;
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var name = Convert.ToString(mo["InstanceName"]) ?? "未知磁盘";
                if (mo["PredictFailure"] is bool predict && predict)
                {
                    failed.Add(name);
                }
                else
                {
                    healthy++;
                }
            }
            if (failed.Count > 0)
            {
                return $"SMART 预警：以下磁盘被标记为预测性故障，请立即备份数据并更换：\n{string.Join("\n", failed.Select(f => $"  {f}"))}";
            }
            return $"SMART 状态：共检查 {healthy + failed.Count} 块磁盘，全部正常。";
        }
        catch
        {
            return "无法读取 SMART 磁盘健康状态（可能需要管理员权限）。";
        }
    }

    private static string GetAppCrashes()
    {
        var events = SystemScanner.QueryEvents("Application",
            "*[System[(EventID=1000 or EventID=1001 or EventID=1002 or EventID=1003 or EventID=1026) and Level<=3 and TimeCreated[timediff(@SystemTime) <= 604800000]]]", 30);

        var groups = events
            .Select(e => new { App = ExtractAppName(e.Description ?? ""), e.Time })
            .Where(x => !string.IsNullOrEmpty(x.App))
            .GroupBy(x => x.App)
            .Select(g => new { App = g.Key, Count = g.Count(), Latest = g.Max(x => x.Time) })
            .OrderByDescending(x => x.Count)
            .Take(8)
            .ToList();

        if (groups.Count == 0)
        {
            return "近 7 天未发现应用程序崩溃记录。";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"近 7 天共 {groups.Sum(g => g.Count)} 次应用程序崩溃：");
        foreach (var g in groups)
        {
            sb.AppendLine($"  {g.App}：{g.Count} 次（最近 {g.Latest:yyyy-MM-dd HH:mm}）");
        }
        return sb.ToString().TrimEnd();
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

    private static string GetPendingReboot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return key != null
                ? "系统更新已安装但等待重启生效。"
                : "无待重启生效的更新。";
        }
        catch
        {
            return "无法检查待重启更新状态。";
        }
    }
}
