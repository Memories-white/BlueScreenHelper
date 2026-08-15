using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.RegularExpressions;
using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BlueScreenHelper.Views;

public sealed partial class DashboardPage : Page
{
    private readonly ObservableCollection<DashboardCrashItem> _crashes = new();

    public DashboardPage()
    {
        InitializeComponent();
        CrashList.ItemsSource = _crashes;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        SysText.Text = "加载中...";
        CrashText.Text = "加载中...";
        await Task.Run(() =>
        {
            var snap = SystemScanner.GetSystemSnapshot();
            var bugChecks = GetCrashStats();
            var power41 = CountPower41();
            var dumpCount = CountDumpFiles();

            DispatcherQueue.TryEnqueue(() =>
            {
                var osName = snap.OSName + (string.IsNullOrEmpty(snap.Edition) ? "" : $"（{snap.Edition}）");
                SysText.Text =
                    $"操作系统：{osName}\n" +
                    $"系统版本：{snap.OSVersion}\n" +
                    $"体系结构：{snap.Arch} · {snap.ProcessorCount} 逻辑处理器\n" +
                    $"制造商：{snap.Manufacturer} {snap.MachineModel}\n" +
                    $"内存：{snap.TotalMemory}（可用 {snap.FreeMemory}，负载 {snap.MemoryLoadPercent}）\n" +
                    $"上次启动：{snap.LastBoot:yyyy-MM-dd HH:mm:ss}\n" +
                    $"已运行：{snap.Uptime}";

                CrashText.Text =
                    $"近 30 天蓝屏次数：{bugChecks.Count}\n" +
                    $"异常断电记录：{power41} 次\n" +
                    $"转储文件数量：{dumpCount}\n" +
                    (bugChecks.Count > 0 ? $"最近一次：{bugChecks[0].Name}\n" : "") +
                    (bugChecks.Count > 0 ? $"错误代码：{bugChecks[0].Code}" : "系统近期稳定");

                _crashes.Clear();
                foreach (var b in bugChecks.Take(8))
                {
                    _crashes.Add(b);
                }
                CrashExpander.IsExpanded = bugChecks.Count > 0;
            });
        });
    }

    private static List<DashboardCrashItem> GetCrashStats()
    {
        var list = new List<DashboardCrashItem>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[(EventID=1001) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]")
            { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            while (true)
            {
                using var record = reader.ReadEvent();
                if (record == null)
                {
                    break;
                }
                string desc = "";
                try
                {
                    desc = record.FormatDescription() ?? "";
                }
                catch
                {
                }
                var m = Regex.Match(desc, @"0x([0-9A-Fa-f]{8})");
                if (!m.Success)
                {
                    continue;
                }
                var code = Convert.ToUInt32(m.Groups[1].Value, 16);
                var entry = BugCheckDatabase.Get(code);
                list.Add(new DashboardCrashItem
                {
                    Code = $"0x{code:X8}",
                    Name = entry?.Name ?? "未知错误",
                    Time = record.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知时间",
                    Detail = desc.Length > 1500 ? desc.Substring(0, 1500) + "\n……" : desc,
                    Solutions = entry?.Solutions
                });
                if (list.Count >= 10)
                {
                    break;
                }
            }
        }
        catch
        {
        }
        return list;
    }

    private static int CountDumpFiles()
    {
        var count = 0;
        try
        {
            var minidump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (Directory.Exists(minidump))
            {
                count += Directory.GetFiles(minidump, "*.dmp").Length;
            }
            var memDump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
            if (File.Exists(memDump))
            {
                count++;
            }
        }
        catch
        {
        }
        return count;
    }

    private static int CountPower41()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]");
            using var reader = new EventLogReader(query);
            var count = 0;
            while (reader.ReadEvent() != null)
            {
                count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private void OpenDump_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateTo("dump");
    }

    private void OpenScanner_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateTo("scanner");
    }

    private void CrashList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = CrashList.SelectedItem as DashboardCrashItem
                   ?? (e.OriginalSource as FrameworkElement)?.DataContext as DashboardCrashItem;
        if (item != null)
        {
            _ = ShowCrashDetail(item);
        }
    }

    private async Task ShowCrashDetail(DashboardCrashItem item)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(MakeRow("错误代码", item.Code, true));
        if (!string.IsNullOrEmpty(item.Name))
        {
            panel.Children.Add(MakeRow("错误名称", item.Name));
        }
        if (!string.IsNullOrEmpty(item.Time))
        {
            panel.Children.Add(MakeRow("崩溃时间", item.Time));
        }
        if (!string.IsNullOrEmpty(item.Detail))
        {
            panel.Children.Add(new TextBlock { Text = "系统事件详情", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = item.Detail,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        if (item.Solutions is { Length: > 0 })
        {
            panel.Children.Add(new TextBlock { Text = "知识库解决方案", FontWeight = FontWeights.SemiBold });
            var sb = new StringBuilder();
            for (int i = 0; i < item.Solutions.Length; i++)
            {
                sb.AppendLine($"{i + 1}. {item.Solutions[i]}");
            }
            panel.Children.Add(new TextBlock { Text = sb.ToString(), TextWrapping = TextWrapping.Wrap });
        }

        var dialog = new ContentDialog
        {
            Title = $"崩溃详情 · {item.Code}",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 460,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = "关闭",
            PrimaryButtonText = "AI 智能诊断",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            AppState.PendingAI = new AIContext
            {
                UserMessage = BuildCrashAiText(item),
                SystemPrompt = "你是一位经验丰富的 Windows 蓝屏(BSOD)诊断专家。用户提供了系统事件日志中的一次蓝屏崩溃记录，" +
                               "请按以下结构用中文回答：\n1. 问题定位（最可能的故障原因）\n2. 详细分析（结合错误代码与事件描述）\n" +
                               "3. 解决方案（按优先级列出可操作步骤）\n4. 预防建议\n若信息不足，请明确指出还需补充什么。"
            };
            App.MainWindow?.NavigateTo("ai");
        }
    }

    private static TextBlock MakeRow(string label, string value, bool bold = false) => new()
    {
        Text = $"{label}：{value}",
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static string BuildCrashAiText(DashboardCrashItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【系统事件日志中的蓝屏记录】");
        sb.AppendLine($"- 错误代码: {item.Code} ({item.Name})");
        sb.AppendLine($"- 崩溃时间: {item.Time}");
        sb.AppendLine("- 系统事件描述:");
        sb.AppendLine(item.Detail);
        return sb.ToString();
    }
}