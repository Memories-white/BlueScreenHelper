using Microsoft.UI.Xaml.Media;

namespace BlueScreenHelper.Models;

public sealed class DashboardCrashItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Time { get; set; } = "";
    public string Detail { get; set; } = "";
    public string[]? Solutions { get; set; }

    public string Summary => string.IsNullOrEmpty(Name) ? Time : $"{Name} · {Time}";
}

public enum IssueSeverity
{
    Info,
    Warning,
    Critical
}

public sealed class ScanIssue
{
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public IssueSeverity Severity { get; set; } = IssueSeverity.Info;
    public string Description { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public DateTime? DetectedAt { get; set; }

    public string TimeText => DetectedAt?.ToString("MM-dd HH:mm") ?? "";

    public string SeverityText => Severity switch
    {
        IssueSeverity.Critical => "严重",
        IssueSeverity.Warning => "警告",
        _ => "提示"
    };

    public Windows.UI.Color SeverityColor => Severity switch
    {
        IssueSeverity.Critical => Windows.UI.Color.FromArgb(255, 232, 17, 35),
        IssueSeverity.Warning => Windows.UI.Color.FromArgb(255, 247, 99, 12),
        _ => Windows.UI.Color.FromArgb(255, 0, 120, 212)
    };

    private Brush? _severityBrush;

    public Brush SeverityBrush => _severityBrush ??= new SolidColorBrush(SeverityColor);
}

public sealed class SystemSnapshot
{
    public string ComputerName { get; set; } = "";
    public string OSName { get; set; } = "";
    public string OSVersion { get; set; } = "";
    public string Build { get; set; } = "";
    public string Edition { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string MachineModel { get; set; } = "";
    public int ProcessorCount { get; set; }
    public string TotalMemory { get; set; } = "";
    public string FreeMemory { get; set; } = "";
    public string MemoryLoadPercent { get; set; } = "";
    public DateTime? LastBoot { get; set; }
    public string Uptime { get; set; } = "";
}

public sealed class ScanReport
{
    public SystemSnapshot System { get; set; } = new();
    public List<ScanIssue> Issues { get; set; } = new();
    public DateTime ScannedAt { get; set; } = DateTime.Now;

    public int CriticalCount => Issues.Count(i => i.Severity == IssueSeverity.Critical);
    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
    public int InfoCount => Issues.Count(i => i.Severity == IssueSeverity.Info);
}