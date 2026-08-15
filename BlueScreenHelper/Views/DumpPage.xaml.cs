using Windows.ApplicationModel.DataTransfer;
using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace BlueScreenHelper.Views;

public sealed partial class DumpPage : Page
{
    private DumpAnalysisResult? _result;

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
            FilePathText.Text = file.Path;
            Analyze(file.Path);
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "打开文件失败";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
    }

    private void Analyze(string path)
    {
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = "正在解析...";
        StatusBar.Message = "读取转储文件二进制数据，请稍候。";
        ResultPanel.Visibility = Visibility.Collapsed;
        ParamsExpander.Visibility = Visibility.Collapsed;
        KnowledgeExpander.Visibility = Visibility.Collapsed;
        ModulesExpander.Visibility = Visibility.Collapsed;
        AiButton.IsEnabled = false;
        CopyButton.IsEnabled = false;

        _ = Task.Run(() => DumpParser.Parse(path)).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    StatusBar.Severity = InfoBarSeverity.Error;
                    StatusBar.Title = "解析失败";
                    StatusBar.Message = t.Exception.GetBaseException().Message;
                    StatusBar.IsOpen = true;
                });
                return;
            }
            var r = t.Result;
            DispatcherQueue.TryEnqueue(() => ShowResult(r));
        });
    }

    private void ShowResult(DumpAnalysisResult r)
    {
        _result = r;
        ResultPanel.Visibility = Visibility.Visible;
        AiButton.IsEnabled = true;
        CopyButton.IsEnabled = true;

        if (!r.IsValidMinidump)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = "无法解析该文件";
            StatusBar.Message = r.Error;
            ResultPanel.Visibility = Visibility.Collapsed;
            ParamsExpander.Visibility = Visibility.Collapsed;
            KnowledgeExpander.Visibility = Visibility.Collapsed;
            ModulesExpander.Visibility = Visibility.Collapsed;
            AiButton.IsEnabled = true;
            return;
        }

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
            ? $"0x{ec:X8} @ 0x{r.ExceptionAddress:X}" + (r.ExceptionAddress.HasValue ? "" : "")
            : "无异常记录";

        var paramMeanings = DumpParser.GetBugCheckParamMeanings(r.BugCheckCode);
        ParamsText.Text = string.Join("\n", r.BugCheckParameters
            .Select((p, i) => $"参数 {i + 1}：0x{p:X}\n        （{(paramMeanings != null && i < paramMeanings.Length ? paramMeanings[i] : "无官方语义注解")}）"));
        ParamsExpander.Visibility = r.IsKernelDump ? Visibility.Visible : Visibility.Collapsed;

        if (r.Knowledge != null)
        {
            DescText.Text = r.Knowledge.Description;
            CausesList.ItemsSource = r.Knowledge.Causes.Select(c => (object)new TextBlock { Text = $"• {c}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            SolutionsList.ItemsSource = r.Knowledge.Solutions.Select((s, i) => (object)new TextBlock { Text = $"{i + 1}. {s}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            KnowledgeExpander.Visibility = Visibility.Visible;
            KnowledgeExpander.IsExpanded = true;
        }
        else
        {
            DescText.Text = "知识库中未收录该错误代码。\n建议使用“AI 智能诊断”获取针对性的分析，或按通用方案排查：更新驱动、检查内存、检查磁盘。";
            CausesList.ItemsSource = null;
            SolutionsList.ItemsSource = null;
            KnowledgeExpander.Visibility = Visibility.Visible;
        }

        if (r.Modules.Count > 0)
        {
            ModulesList.ItemsSource = r.Modules;
            ModulesExpander.Visibility = Visibility.Visible;
        }
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null)
        {
            return;
        }
        var text = DumpParser.BuildReportMarkdown(_result);
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Title = "已复制";
        StatusBar.Message = "分析报告（Markdown 格式）已复制到剪贴板，可粘贴到文档或 AI 对话中。";
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
                           "3. 解决方案（按优先级列出可操作步骤，包含具体命令或工具）\n4. 预防建议\n若信息不足，请明确指出还需补充什么。"
        };
        App.MainWindow?.NavigateTo("ai");
    }
}