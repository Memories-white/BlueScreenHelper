using System.Diagnostics;
using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueScreenHelper.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly string[] ModelPresets =
    {
        "gpt-4o-mini", "gpt-4o", "deepseek-chat", "deepseek-reasoner",
        "glm-4-flash", "glm-4-plus", "moonshot-v1-8k", "qwen-plus", "qwen-turbo",
        "Qwen/Qwen2.5-7B-Instruct", "Meta-Llama-3.1-70B-Instruct", "llama3.1",
        "claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5",
        "gemini-2.0-flash", "gemini-1.5-flash", "gemini-2.5-pro"
    };

    private AIConfigItem? _current;

    public SettingsPage()
    {
        InitializeComponent();
        ModelBox.ItemsSource = ModelPresets;
        TempSlider.ValueChanged += (_, _) => TempValueText.Text = DescribeTemperature(TempSlider.Value);
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = $"蓝屏诊断助手 v{UpdaterService.CurrentVersion} — WinUI 3";
        TempValueText.Text = DescribeTemperature(TempSlider.Value);
        RefreshConfigs(selectActive: true);
    }

    private void RefreshConfigs(bool selectActive)
    {
        ConfigBox.ItemsSource = null;
        ConfigBox.ItemsSource = AppState.Settings.AIConfigs;
        if (AppState.Settings.AIConfigs.Count == 0)
        {
            LoadConfigToFields(null);
            return;
        }
        var target = AppState.Settings.ActiveConfig ?? AppState.Settings.AIConfigs[0];
        ConfigBox.SelectedItem = selectActive ? target : AppState.Settings.AIConfigs[0];
    }

    private void ConfigBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var next = ConfigBox.SelectedItem as AIConfigItem;
        if (next == _current)
        {
            return;
        }
        if (_current != null)
        {
            WriteFieldsToConfig(_current);
        }
        LoadConfigToFields(next);
    }

    private void LoadConfigToFields(AIConfigItem? cfg)
    {
        _current = cfg;
        var has = cfg != null;
        ProviderText.Text = has ? $"协议：{cfg!.ProviderDisplay}" : "尚未创建配置，点击“新建配置”选择预设模板。";
        NameBox.Text = has ? cfg!.Name : "";
        BaseUrlBox.Text = has ? cfg!.ApiBaseUrl : "";
        ApiKeyBox.Password = has ? cfg!.ApiKey : "";
        var model = cfg?.Model ?? "";
        var presets = ModelPresets.ToList();
        if (!string.IsNullOrWhiteSpace(model) && !presets.Contains(model))
        {
            presets.Insert(0, model);
        }
        ModelBox.ItemsSource = presets;
        if (presets.Contains(model))
        {
            ModelBox.SelectedItem = model;
        }
        else
        {
            ModelBox.Text = model;
        }
        TempSlider.Value = has ? cfg!.Temperature : 0.3;
        PromptBox.Text = has ? cfg!.SystemPrompt : "";
        TempValueText.Text = DescribeTemperature(TempSlider.Value);
        DeleteButton.IsEnabled = has;
        SaveButton.IsEnabled = has;
        TestButton.IsEnabled = has;
    }

    private void WriteFieldsToConfig(AIConfigItem cfg)
    {
        cfg.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? cfg.Name : NameBox.Text.Trim();
        cfg.ApiBaseUrl = BaseUrlBox.Text.Trim();
        cfg.ApiKey = ApiKeyBox.Password.Trim();
        cfg.Model = ModelBox.Text.Trim();
        cfg.Temperature = TempSlider.Value;
        cfg.SystemPrompt = PromptBox.Text;
    }

    private void PresetItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
        {
            return;
        }
        var template = AIPresets.Templates.FirstOrDefault(t => t.Name == tag);
        if (template == null)
        {
            return;
        }
        var cfg = new AIConfigItem
        {
            Name = UniqueName(template.Name),
            Provider = template.Provider,
            ApiBaseUrl = template.ApiBaseUrl,
            ApiKey = template.ApiKey,
            Model = template.Model,
            Temperature = template.Temperature,
            SystemPrompt = template.SystemPrompt
        };
        AppState.Settings.AIConfigs.Add(cfg);
        AppState.Settings.ActiveConfigName = cfg.Name;
        RefreshConfigs(selectActive: true);
    }

    private string UniqueName(string baseName)
    {
        var existing = AppState.Settings.AIConfigs.Select(c => c.Name).ToHashSet();
        if (!existing.Contains(baseName))
        {
            return baseName;
        }
        var i = 2;
        while (existing.Contains($"{baseName} {i}"))
        {
            i++;
        }
        return $"{baseName} {i}";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        var dlg = new ContentDialog
        {
            Title = "删除配置",
            Content = $"确定要删除 AI 配置“{_current.Name}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        AppState.Settings.AIConfigs.Remove(_current);
        if (AppState.Settings.ActiveConfigName == _current.Name)
        {
            AppState.Settings.ActiveConfigName = AppState.Settings.AIConfigs.FirstOrDefault()?.Name ?? "";
        }
        AppState.Settings.Save();
        RefreshConfigs(selectActive: true);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowBar(InfoBarSeverity.Warning, "配置名称不能为空", "请先填写配置名称（例如“我的 OpenAI”）。");
            return;
        }
        if (string.IsNullOrWhiteSpace(BaseUrlBox.Text))
        {
            ShowBar(InfoBarSeverity.Warning, "接口地址不能为空", "请填写接口地址 (Base URL)。");
            return;
        }
        WriteFieldsToConfig(_current);
        AppState.Settings.ActiveConfigName = _current.Name;
        AppState.Settings.Save();
        RefreshConfigs(selectActive: true);
        ShowBar(InfoBarSeverity.Success, "配置已保存",
            $"配置文件位置：{AppSettings.ConfigFilePath}\n该配置现已出现在“AI 诊断助手”页面的下拉列表中。");
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(BaseUrlBox.Text))
        {
            ShowBar(InfoBarSeverity.Warning, "接口地址不能为空", "请先填写接口地址 (Base URL)。");
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password) && _current.Provider != AIProvider.Custom)
        {
            ShowBar(InfoBarSeverity.Warning, "尚未填写 API Key",
                "该协议需要 API Key。自定义（OpenAI 兼容）的本地服务如 Ollama 可留空。");
            return;
        }

        WriteFieldsToConfig(_current);
        TestButton.IsEnabled = false;
        TestRing.IsActive = true;
        SaveBar.IsOpen = false;

        try
        {
            var ai = new AIService();
            var result = await ai.TestConnectionAsync(_current);
            ShowBar(InfoBarSeverity.Success, "连接成功", $"接口与模型可用，返回：{result}");
        }
        catch (Exception ex)
        {
            ShowBar(InfoBarSeverity.Error, "连接失败", ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestRing.IsActive = false;
            SaveBar.IsOpen = true;
        }
    }

    private void ShowBar(InfoBarSeverity severity, string title, string message)
    {
        SaveBar.Severity = severity;
        SaveBar.Title = title;
        SaveBar.Message = message;
        SaveBar.IsOpen = true;
    }

    private static string DescribeTemperature(double v) => v switch
    {
        < 0.2 => "0（严谨收敛：回答固定、逻辑严密，适合故障定位）",
        < 0.5 => "0.3（平衡：严谨为主，略有发散）",
        < 0.8 => "0.6（平衡偏发散：表述更自由）",
        _ => "1（发散多样：创意更强，但可能不够稳定）"
    };

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLogger.LogDir}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowBar(InfoBarSeverity.Error, "无法打开日志文件夹", ex.Message);
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateRing.IsActive = true;
        UpdateText.Text = "正在检查更新…";
        DownloadBar.Visibility = Visibility.Collapsed;

        try
        {
            var info = await UpdaterService.CheckAsync();
            var local = Version.Parse(UpdaterService.CurrentVersion);

            if (info == null || info.LatestVersion <= local)
            {
                UpdateText.Text = $"当前已是最新版本（v{local}）。";
                return;
            }

            var notes = info.ReleaseNotes.Trim();
            if (notes.Length > 600)
            {
                notes = notes[..600] + "…";
            }
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = $"当前版本：v{local}　→　最新版本：v{info.LatestVersion}", TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(notes))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = notes,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
            }

            var dlg = new ContentDialog
            {
                Title = "发现新版本",
                Content = panel,
                PrimaryButtonText = "下载并更新",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
            {
                UpdateText.Text = "已取消更新。";
                return;
            }

            DownloadBar.Visibility = Visibility.Visible;
            DownloadBar.Value = 0;
            UpdateText.Text = "正在下载安装包…";
            var path = await UpdaterService.DownloadAsync(info.InstallerUrl,
                new Progress<double>(p => DownloadBar.Value = p));

            UpdateText.Text = "下载完成，等待确认…";
            var installDlg = new ContentDialog
            {
                Title = "安装包已下载",
                Content = "即将退出本应用并启动安装向导。\n安装完成后将自动打开新版本。",
                PrimaryButtonText = "开始安装",
                CloseButtonText = "稍后安装",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await installDlg.ShowAsync() != ContentDialogResult.Primary)
            {
                UpdateText.Text = "安装包已下载到临时目录，可稍后手动运行安装。";
                return;
            }

            UpdateText.Text = "正在启动安装向导，本应用即将关闭。";
            UpdaterService.LaunchInstaller(path);
            await Task.Delay(1500);
            App.MainWindow?.Close();
        }
        catch (Exception ex)
        {
            UpdateText.Text = ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            UpdateRing.IsActive = false;
            DownloadBar.Visibility = Visibility.Collapsed;
        }
    }
}
