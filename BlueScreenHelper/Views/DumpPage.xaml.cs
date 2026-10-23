using Windows.ApplicationModel.DataTransfer;
using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;

namespace BlueScreenHelper.Views;

public sealed partial class DumpPage : Page
{
    private DumpAnalysisResult? _result;
    private long _analysisVersion;

    public DumpPage()
    {
        InitializeComponent();
    }

    private async void PickFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(Win32Interop.GetWindowIdFromWindow(App.MainWindowHandle))
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".dmp");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }
            await AnalyzeAsync(file.Path);
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "打开文件失败";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
    }

    private async Task AnalyzeAsync(string path)
    {
        var version = Interlocked.Increment(ref _analysisVersion);
        _result = null;
        FilePathText.Text = path;
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = "正在解析...";
        StatusBar.Message = "读取转储文件二进制数据，请稍候。";
        ResultPanel.Visibility = Visibility.Collapsed;
        ParamsExpander.Visibility = Visibility.Collapsed;
        KnowledgeExpander.Visibility = Visibility.Collapsed;
        ModulesExpander.Visibility = Visibility.Collapsed;
        ParamsExpander.IsExpanded = false;
        KnowledgeExpander.IsExpanded = false;
        ModulesExpander.IsExpanded = false;
        ModulesList.ItemsSource = null;
        CausesList.Children.Clear();
        SolutionsList.Children.Clear();
        SafetyNotesList.Children.Clear();
        VerificationList.Children.Clear();
        SafetyInfoBar.IsOpen = false;
        VerificationSection.Visibility = Visibility.Collapsed;
        AiButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        PickButton.IsEnabled = false;
        AnalyzeRing.IsActive = true;

        try
        {
            var result = await Task.Run(() => DumpParser.Parse(path));
            if (version == _analysisVersion)
            {
                ShowResult(result);
            }
        }
        catch (Exception ex)
        {
            if (version == _analysisVersion)
            {
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.Title = "解析失败";
                StatusBar.Message = ex.Message;
                StatusBar.IsOpen = true;
            }
        }
        finally
        {
            if (version == _analysisVersion)
            {
                PickButton.IsEnabled = true;
                AnalyzeRing.IsActive = false;
            }
        }
    }

    private void ShowResult(DumpAnalysisResult r)
    {
        if (!r.IsValidMinidump)
        {
            _result = null;
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = "无法解析该文件";
            StatusBar.Message = string.IsNullOrWhiteSpace(r.Error) ? "文件不是受支持的 Windows 转储格式。" : r.Error;
            ResultPanel.Visibility = Visibility.Collapsed;
            ParamsExpander.Visibility = Visibility.Collapsed;
            KnowledgeExpander.Visibility = Visibility.Collapsed;
            ModulesExpander.Visibility = Visibility.Collapsed;
            AiButton.IsEnabled = false;
            CopyButton.IsEnabled = false;
            return;
        }

        _result = r;
        ResultPanel.Visibility = Visibility.Visible;
        AiButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Title = r.IsKernelDump ? "解析完成：系统蓝屏转储" : "解析完成：用户态转储";
        StatusBar.Message = r.IsKernelDump
            ? $"已识别为{r.DumpTypeText}（{DumpParser.FormatSize(r.FileSize)}），提取出崩溃代码 0x{r.BugCheckCode:X8}。"
            : $"文件大小 {DumpParser.FormatSize(r.FileSize)}，共解析到 {r.Modules.Count} 个模块。";

        CodeText.Text = r.BugCheckHex;
        NameText.Text = string.IsNullOrEmpty(r.BugCheckName) ? r.Knowledge?.Name ?? "未知" : r.BugCheckName;
        CategoryText.Text = r.Knowledge?.Category ?? "未知";
        ModuleText.Text = string.IsNullOrEmpty(r.FaultingModule) ? "未定位（可尝试 AI 诊断）" : r.FaultingModule;
        TimeText.Text = r.DumpTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? r.FileModified.ToString("yyyy-MM-dd HH:mm:ss");
        OsText.Text = r.OSVersion ?? "未知";
        MiscText.Text = r.IsKernelDump
            ? $"{r.DumpTypeText}（{r.ProcessorArch}）"
            : $"用户态转储{((r.ProcessId is uint pid) ? $" · 进程 ID：{pid}" : "")}";
        ExText.Text = r.ExceptionCode is uint ec
            ? r.ExceptionAddress is ulong address
                ? $"0x{ec:X8} @ 0x{address:X}"
                : $"0x{ec:X8}（地址未知）"
            : "无异常记录";

        var paramMeanings = DumpParser.GetBugCheckParamMeanings(r.BugCheckCode);
        ParamsText.Text = string.Join("\n", r.BugCheckParameters
            .Select((p, i) => $"参数 {i + 1}：0x{p:X}\n        （{(paramMeanings != null && i < paramMeanings.Length ? paramMeanings[i] : "无官方语义注解")}）"));
        ParamsExpander.Visibility = r.IsKernelDump ? Visibility.Visible : Visibility.Collapsed;

        if (r.Knowledge != null)
        {
            DescText.Text = r.Knowledge.Description;
            PopulateGuidanceList(CausesList, r.Knowledge.Causes);
            PopulateGuidanceList(
                SolutionsList,
                BugCheckDatabase.BuildPrioritizedSolutions(r.Knowledge, r.FaultingModule),
                numbered: true);
            PopulateGuidanceList(SafetyNotesList, r.Knowledge.SafetyNotes);
            SafetyInfoBar.IsOpen = r.Knowledge.SafetyNotes.Length > 0;
            PopulateGuidanceList(VerificationList, r.Knowledge.VerificationSteps, numbered: true);
            VerificationSection.Visibility = r.Knowledge.VerificationSteps.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            KnowledgeExpander.Visibility = Visibility.Visible;
            KnowledgeExpander.IsExpanded = true;
        }
        else
        {
            DescText.Text = "知识库中尚未收录该错误代码。下面提供的是通用、低风险的排查顺序；它不能替代针对错误参数和故障模块的判断。";
            PopulateGuidanceList(CausesList, new[]
            {
                "新版本或较少见的驱动、硬件或系统错误",
                "转储信息不足，无法仅凭错误代码确定根因"
            });
            PopulateGuidanceList(
                SolutionsList,
                BugCheckDatabase.BuildGeneralSolutions(r.FaultingModule),
                numbered: true);
            PopulateGuidanceList(SafetyNotesList, new[]
            {
                "不要根据单次蓝屏就批量卸载驱动或修改 BIOS；先保留转储并观察是否复现。",
                "涉及磁盘修复、固件更新或硬件拆装前，请先备份重要数据并准备恢复方式。"
            });
            SafetyInfoBar.IsOpen = true;
            PopulateGuidanceList(VerificationList, new[]
            {
                "重启后按原使用场景观察，确认问题是否在相同操作下复现。",
                "检查事件查看器和 C:\\Windows\\Minidump，确认没有生成新的同类崩溃记录。",
                "若再次蓝屏，比较新的错误代码、参数和故障模块，再决定下一步。"
            }, numbered: true);
            VerificationSection.Visibility = Visibility.Visible;
            KnowledgeExpander.Visibility = Visibility.Visible;
            KnowledgeExpander.IsExpanded = true;
        }

        if (r.Modules.Count > 0)
        {
            ModulesList.ItemsSource = r.Modules;
            ModulesExpander.Visibility = Visibility.Visible;
        }
    }

    private static void PopulateGuidanceList(
        StackPanel target,
        IEnumerable<string> items,
        bool numbered = false)
    {
        target.Children.Clear();
        var index = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            index++;
            var row = new Grid
            {
                ColumnSpacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = numbered ? $"{index}." : "•",
                Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Top
            };
            var content = new TextBlock
            {
                Text = item.Trim(),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            target.Children.Add(row);
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "打开转储文件";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file == null || !string.Equals(file.FileType, ".dmp", StringComparison.OrdinalIgnoreCase))
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Title = "不支持的文件";
                StatusBar.Message = "请拖入扩展名为 .dmp 的 Windows 转储文件。";
                StatusBar.IsOpen = true;
                return;
            }
            await AnalyzeAsync(file.Path);
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "打开文件失败";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null)
        {
            return;
        }
        try
        {
            var text = DumpParser.BuildReportMarkdown(_result);
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Title = "已复制";
            StatusBar.Message = "分析报告（Markdown 格式）已复制到剪贴板，可粘贴到文档或 AI 对话中。";
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "复制失败";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
    }

    private void AiDiagnose_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null)
        {
            return;
        }
        AppState.PendingAI = new AIContext
        {
            UserMessage = DumpParser.BuildAnalysisText(_result),
            SystemPrompt = "你是一位经验丰富的 Windows 蓝屏(BSOD)诊断专家。用户会提供 .dmp 转储文件的解析数据。" +
                           "请按以下结构用中文回答：\n1. 问题定位（最可能的故障原因）\n2. 详细分析（结合错误代码与参数）\n" +
                           "3. 解决方案（按优先级列出可操作步骤，包含具体命令或工具）\n4. 修复验证\n5. 预防建议\n" +
                           "请明确区分转储证据、合理推断和未知信息；优先给出可逆、低风险步骤。涉及磁盘写入、驱动验证器、BIOS/固件、注册表或硬件拆装时，必须说明备份、恢复和停止条件。若信息不足，请明确指出还需补充什么。"
        };
        App.MainWindow?.NavigateTo("ai");
    }
}
