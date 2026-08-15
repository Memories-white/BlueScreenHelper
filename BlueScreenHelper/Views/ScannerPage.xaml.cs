using System.Collections.ObjectModel;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueScreenHelper.Views;

public sealed partial class ScannerPage : Page
{
    private readonly ObservableCollection<ScanIssue> _visibleIssues = new();
    private ScanReport? _report;
    private bool _scanning;

    public ScannerPage()
    {
        InitializeComponent();
        IssueList.ItemsSource = _visibleIssues;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning)
        {
            return;
        }
        _scanning = true;
        ScanButton.IsEnabled = false;
        ScanRing.IsActive = true;
        StatusText.Text = "正在扫描系统...";
        SummaryBar.IsOpen = false;
        _visibleIssues.Clear();
        EmptyText.Text = "正在扫描...";

        try
        {
            var report = await Task.Run(() => SystemScanner.ScanAll());
            ShowReport(report);
            StatusText.Text = "扫描完成";
        }
        catch (Exception ex)
        {
            SummaryBar.Severity = InfoBarSeverity.Error;
            SummaryBar.Title = "扫描失败";
            SummaryBar.Message = ex.Message;
            SummaryBar.IsOpen = true;
            EmptyText.Text = "扫描失败，请重试";
            StatusText.Text = "扫描失败";
        }
        finally
        {
            _scanning = false;
            ScanButton.IsEnabled = true;
            ScanRing.IsActive = false;
        }
    }

    private void ShowReport(ScanReport report)
    {
        _report = report;
        EmptyText.Text = "未发现任何问题";
        ApplyFilter();

        var snap = report.System;
        SysText.Text =
            $"操作系统：{snap.OSName}（{snap.Edition}）\n" +
            $"系统版本：{snap.OSVersion}\n" +
            $"体系结构：{snap.Arch} · {snap.ProcessorCount} 逻辑处理器\n" +
            $"设备：{snap.Manufacturer} {snap.MachineModel}\n" +
            $"内存：{snap.TotalMemory}（可用 {snap.FreeMemory}）\n" +
            $"已运行：{snap.Uptime}（上次启动 {snap.LastBoot:MM-dd HH:mm}）";

        SummaryBar.Severity = report.CriticalCount > 0 ? InfoBarSeverity.Error :
            report.WarningCount > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        SummaryBar.Title = $"扫描完成：发现 {report.CriticalCount} 个严重问题、{report.WarningCount} 个警告、{report.InfoCount} 条提示";
        SummaryBar.Message = report.CriticalCount > 0
            ? "发现严重问题，建议优先处理，并使用“AI 分析扫描结果”获取详细修复方案。"
            : "系统整体状态良好。";
        SummaryBar.IsOpen = true;
    }

    private void ApplyFilter()
    {
        if (_report == null)
        {
            return;
        }
        var tag = (FilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var all = _report.Issues;
        IEnumerable<ScanIssue> filtered = tag switch
        {
            "critical" => all.Where(i => i.Severity == IssueSeverity.Critical),
            "warning" => all.Where(i => i.Severity != IssueSeverity.Info),
            "info" => all.Where(i => i.Severity == IssueSeverity.Info),
            _ => all
        };
        _visibleIssues.Clear();
        foreach (var i in filtered)
        {
            _visibleIssues.Add(i);
        }
        EmptyText.Text = _report.Issues.Count == 0 ? "未发现任何问题" : "当前筛选条件下没有结果";
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    private string BuildReportText()
    {
        if (_report == null)
        {
            return "";
        }
        var sb = new StringBuilder();
        sb.AppendLine("# 系统扫描报告");
        sb.AppendLine();
        sb.AppendLine($"- 扫描时间：{_report.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        var snap = _report.System;
        sb.AppendLine($"- 系统：{snap.OSName} {snap.OSVersion}（{snap.Edition}）");
        sb.AppendLine($"- 设备：{snap.Manufacturer} {snap.MachineModel} · {snap.Arch}");
        sb.AppendLine($"- 内存：{snap.TotalMemory}，已运行 {snap.Uptime}");
        sb.AppendLine();
        foreach (var severity in new[] { IssueSeverity.Critical, IssueSeverity.Warning, IssueSeverity.Info })
        {
            var items = _report.Issues.Where(i => i.Severity == severity).ToList();
            if (items.Count == 0)
            {
                continue;
            }
            sb.AppendLine($"## {items[0].SeverityText}问题（{items.Count} 项）");
            sb.AppendLine();
            foreach (var i in items)
            {
                sb.AppendLine($"### [{i.Category}] {i.Title}");
                sb.AppendLine();
                sb.AppendLine(i.Description);
                sb.AppendLine();
                sb.AppendLine($"建议：{i.Recommendation}");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private void CopyScan_Click(object sender, RoutedEventArgs e)
    {
        var text = BuildReportText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        SummaryBar.Severity = InfoBarSeverity.Success;
        SummaryBar.Title = "已复制";
        SummaryBar.Message = "扫描报告（Markdown）已复制到剪贴板。";
        SummaryBar.IsOpen = true;
    }

    private void AiScan_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null)
        {
            return;
        }
        AppState.PendingAI = new AIContext
        {
            UserMessage = BuildAIAnalysisText(),
            SystemPrompt = "你是一位 Windows 系统稳定性和故障诊断专家。用户提供了系统扫描报告，" +
                           "请用中文分析：1) 最可能导致系统崩溃/不稳定的问题（按严重程度排序）\n" +
                           "2) 每个问题的具体修复步骤（包含命令与工具）\n" +
                           "3) 需要进一步检查的项目"
        };
        App.MainWindow?.NavigateTo("ai");
    }

    private string BuildAIAnalysisText()
    {
        if (_report == null)
        {
            return "";
        }
        var sb = new StringBuilder();
        sb.AppendLine("【系统扫描报告】");
        var snap = _report.System;
        sb.AppendLine($"- 系统：{snap.OSName} {snap.OSVersion}");
        sb.AppendLine($"- 设备：{snap.Manufacturer} {snap.MachineModel}");
        sb.AppendLine($"- 内存：{snap.TotalMemory}（可用 {snap.FreeMemory}）");
        sb.AppendLine($"- 运行时长：{snap.Uptime}");
        sb.AppendLine("- 发现的问题：");
        foreach (var i in _report.Issues)
        {
            sb.AppendLine($"  [{i.SeverityText}][{i.Category}] {i.Title}");
            sb.AppendLine($"    - 详情：{i.Description}");
            sb.AppendLine($"    - 建议：{i.Recommendation}");
        }
        return sb.ToString();
    }
}