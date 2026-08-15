using System.Collections.ObjectModel;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        filtered = SortTime.IsChecked == true
            ? filtered.OrderByDescending(i => i.DetectedAt ?? DateTime.MinValue)
            : filtered.OrderByDescending(i => i.Severity).ThenByDescending(i => i.DetectedAt ?? DateTime.MinValue);
        _visibleIssues.Clear();
        foreach (var i in filtered)
        {
            _visibleIssues.Add(i);
        }
        EmptyText.Text = _report.Issues.Count == 0 ? "未发现任何问题" : "当前筛选条件下没有结果";
    }

    private void Sort_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
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

    private async void IssueDetail_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as ScanIssue;
        if (item == null)
        {
            return;
        }
        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        var title = new TextBlock { Text = item.Title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(title);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new Border
                {
                    Background = item.SeverityBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Child = new TextBlock { Text = item.SeverityText, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12 }
                },
                new TextBlock { Text = item.Category, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                new TextBlock { Text = string.IsNullOrEmpty(item.TimeText) ? "" : $"时间：{item.TimeText}", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] }
            }
        });

        if (!string.IsNullOrEmpty(item.Description))
        {
            panel.Children.Add(new TextBlock { Text = "问题描述", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap });
        }
        if (!string.IsNullOrEmpty(item.Detail))
        {
            panel.Children.Add(new TextBlock { Text = "详细分析", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = item.Detail,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                LineHeight = 18
            });
        }
        if (!string.IsNullOrEmpty(item.Recommendation))
        {
            panel.Children.Add(new TextBlock { Text = "建议处理方案", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = item.Recommendation, TextWrapping = TextWrapping.Wrap });
        }

        var dialog = new ContentDialog
        {
            Title = $"扫描详情 · {item.Title}",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = "关闭",
            PrimaryButtonText = "AI 分析",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            SendIssueToAi(item);
        }
    }

    private void IssueAi_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as ScanIssue;
        if (item != null)
        {
            SendIssueToAi(item);
        }
    }

    private void SendIssueToAi(ScanIssue item)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【系统扫描发现的问题】");
        sb.AppendLine($"- 严重程度：{item.SeverityText}");
        sb.AppendLine($"- 分类：{item.Category}");
        if (item.DetectedAt != null)
        {
            sb.AppendLine($"- 时间：{item.DetectedAt:yyyy-MM-dd HH:mm}");
        }
        sb.AppendLine($"- 问题：{item.Title}");
        sb.AppendLine($"- 详情：{item.Description}");
        if (!string.IsNullOrEmpty(item.Detail))
        {
            sb.AppendLine("- 详细数据：");
            sb.AppendLine(item.Detail);
        }
        sb.AppendLine($"- 现有建议：{item.Recommendation}");
        AppState.PendingAI = new AIContext
        {
            UserMessage = sb.ToString(),
            SystemPrompt = "你是一位 Windows 系统稳定性和故障诊断专家。用户提供了系统扫描发现的其中一个具体问题，" +
                           "请用中文分析：1) 问题成因 2) 具体修复步骤（包含命令与工具）\n" +
                           "3) 如何验证问题是否已解决 4) 预防措施"
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