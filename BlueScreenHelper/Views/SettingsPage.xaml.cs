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
        "deepseek-flash", "deepseek-v4-pro"
    };

    private static readonly string[] PreferredModels =
    {
        "deepseek-flash", "deepseek-v4-pro"
    };

    private static readonly string[] ExcludedModelTerms =
    {
        "embed", "rerank", "whisper", "tts", "moderation", "image", "dall-e"
    };

    private readonly AIService _ai = new();
    private AIConfigItem? _current;
    private CancellationTokenSource? _modelFetchCts;
    private bool _syncingPreset;
    private bool _loadingFields;
    private bool _fetchingModels;

    public SettingsPage()
    {
        InitializeComponent();
        PresetBox.ItemsSource = AIPresets.Templates;
        ModelBox.ItemsSource = ModelPresets;
        TempSlider.ValueChanged += (_, _) => TempValueText.Text = DescribeTemperature(TempSlider.Value);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = $"蓝屏诊断助手 v{UpdaterService.CurrentVersion} — WinUI 3";
        TempValueText.Text = DescribeTemperature(TempSlider.Value);
        RefreshConfigs();
        if (AppState.Settings.AIConfigs.Count == 0)
        {
            PresetBox.SelectedIndex = 0;
        }
        await SyncVersionWithGitHubAsync();
    }

    private async Task SyncVersionWithGitHubAsync()
    {
        try
        {
            var info = await UpdaterService.CheckAsync();
            if (info != null)
            {
                VersionText.Text = $"蓝屏诊断助手 v{UpdaterService.CurrentVersion} — WinUI 3（GitHub 最新：v{info.LatestVersion}）";
            }
        }
        catch
        {
        }
    }

    private void RefreshConfigs()
    {
        if (AppState.Settings.AIConfigs.Count == 0)
        {
            LoadConfigToFields(null);
            return;
        }
        var target = AppState.Settings.ActiveConfig ?? AppState.Settings.AIConfigs[0];
        LoadConfigToFields(target);
    }

    private void LoadConfigToFields(AIConfigItem? cfg)
    {
        _loadingFields = true;
        try
        {
            CancelModelFetch();
            _current = cfg;
            SyncPresetSelection(cfg);
            var has = cfg != null;
            BaseUrlBox.Text = has ? DeepSeekEndpoints.ForProvider(cfg!.Provider) : "";
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
            SaveButton.IsEnabled = has;
            TestButton.IsEnabled = has;
        }
        finally
        {
            _loadingFields = false;
            UpdateFetchModelsState(resetStatus: true);
            ScheduleAutomaticModelFetch();
        }
    }

    private void WriteFieldsToConfig(AIConfigItem cfg)
    {
        cfg.Provider = DeepSeekEndpoints.NormalizeProvider(cfg.Provider);
        cfg.ApiBaseUrl = DeepSeekEndpoints.ForProvider(cfg.Provider);
        cfg.ApiKey = ApiKeyBox.Password.Trim();
        cfg.Model = ModelBox.Text.Trim();
        cfg.Temperature = TempSlider.Value;
        cfg.SystemPrompt = PromptBox.Text;
    }

    private void SyncPresetSelection(AIConfigItem? cfg)
    {
        _syncingPreset = true;
        try
        {
            var index = -1;
            if (cfg != null)
            {
                for (var i = 0; i < PresetBox.Items.Count; i++)
                {
                    if (PresetBox.Items[i] is AIConfigItem t && t.Provider == cfg.Provider)
                    {
                        index = i;
                        break;
                    }
                }
            }
            PresetBox.SelectedIndex = index;
        }
        finally
        {
            _syncingPreset = false;
        }
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPreset || PresetBox.SelectedItem is not AIConfigItem template)
        {
            return;
        }

        var cfg = AppState.Settings.AIConfigs.FirstOrDefault(c => c.Provider == template.Provider);
        if (cfg == null)
        {
            cfg = new AIConfigItem
            {
                Name = template.Name,
                Provider = template.Provider,
                ApiBaseUrl = template.ApiBaseUrl
            };
            AppState.Settings.AIConfigs.Add(cfg);
        }
        else
        {
            cfg.ApiBaseUrl = DeepSeekEndpoints.ForProvider(cfg.Provider);
        }
        AppState.Settings.ActiveConfigName = cfg.Name;
        RefreshConfigs();
    }

    private void UpdateFetchModelsState(bool resetStatus = false)
    {
        FetchModelsButton.IsEnabled = CanFetchModelsAutomatically() && !_fetchingModels;
        if (!_fetchingModels && resetStatus)
        {
            AutoModelStatusText.Text = "填写 DeepSeek API Key 后，将自动查询并选择可用模型。";
        }
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            ShowBar(InfoBarSeverity.Warning, "尚未选择 AI 服务", "请先选择一个 AI 服务预设。");
            return;
        }

        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            ShowBar(InfoBarSeverity.Warning, "尚未填写 API Key", "请先填写 DeepSeek API Key。");
            return;
        }

        await StartModelFetchAsync(automatic: false);
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ScheduleAutomaticModelFetch();
    }

    private void ScheduleAutomaticModelFetch()
    {
        if (_loadingFields)
        {
            return;
        }

        CancelModelFetch();
        if (!CanFetchModelsAutomatically())
        {
            UpdateFetchModelsState();
            return;
        }

        AutoModelStatusText.Text = "等待输入完成后自动查询模型…";
        _ = StartModelFetchAsync(automatic: true);
    }

    private bool CanFetchModelsAutomatically()
    {
        return _current != null && !string.IsNullOrWhiteSpace(ApiKeyBox.Password);
    }

    private async Task StartModelFetchAsync(bool automatic)
    {
        CancelModelFetch();
        var cts = new CancellationTokenSource();
        _modelFetchCts = cts;

        try
        {
            if (automatic)
            {
                await Task.Delay(700, cts.Token);
            }

            _fetchingModels = true;
            FetchModelsButton.IsEnabled = false;
            ModelFetchRing.IsActive = true;
            AutoModelStatusText.Text = automatic ? "正在自动查询可用模型…" : "正在查询可用模型…";
            SaveBar.IsOpen = false;

            var config = new AIConfigItem
            {
                Provider = _current?.Provider ?? AIProvider.OpenAI,
                ApiBaseUrl = DeepSeekEndpoints.ForProvider(_current?.Provider ?? AIProvider.OpenAI),
                ApiKey = ApiKeyBox.Password.Trim()
            };

            var models = await _ai.ListModelsAsync(config, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var selectedModel = ApplyAvailableModels(models);
            AutoModelStatusText.Text = $"已查询到 {models.Count} 个模型，已选择 {selectedModel}。";

            if (automatic)
            {
                ShowBar(InfoBarSeverity.Success, "已自动查询模型",
                    $"已选择 {selectedModel}；确认后请点击“保存配置”。");
            }
            else
            {
                ShowBar(InfoBarSeverity.Success, $"查询到 {models.Count} 个可用模型",
                    $"已选择 {selectedModel}，也可以从下拉列表中更换。");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AutoModelStatusText.Text = $"{(automatic ? "自动查询" : "查询")}失败：{ex.Message}";
            if (!automatic)
            {
                ShowBar(InfoBarSeverity.Error, "查询模型失败", ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_modelFetchCts, cts))
            {
                _modelFetchCts = null;
                _fetchingModels = false;
                ModelFetchRing.IsActive = false;
                UpdateFetchModelsState();
            }
            cts.Dispose();
        }
    }

    private string ApplyAvailableModels(IReadOnlyList<string> models)
    {
        var currentModel = ModelBox.Text.Trim();
        var selectedModel = models.FirstOrDefault(model =>
                                model.Equals(currentModel, StringComparison.OrdinalIgnoreCase))
                            ?? PreferredModels.Select(preferred => models.FirstOrDefault(model =>
                                    model.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
                                .FirstOrDefault(model => model != null)
                            ?? models.FirstOrDefault(IsLikelyChatModel)
                            ?? models[0];

        ModelBox.ItemsSource = models;
        ModelBox.SelectedItem = selectedModel;
        return selectedModel;
    }

    private static bool IsLikelyChatModel(string model)
    {
        return !ExcludedModelTerms.Any(term => model.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void CancelModelFetch()
    {
        var cts = _modelFetchCts;
        _modelFetchCts = null;
        if (cts == null)
        {
            return;
        }

        cts.Cancel();
        _fetchingModels = false;
        ModelFetchRing.IsActive = false;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelModelFetch();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        WriteFieldsToConfig(_current);
        AppState.Settings.ActiveConfigName = _current.Name;
        AppState.Settings.Save();
        RefreshConfigs();
        ShowBar(InfoBarSeverity.Success, "配置已保存",
            $"配置文件位置：{AppSettings.ConfigFilePath}\n该配置现已出现在“AI 诊断助手”页面的下拉列表中。");
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            ShowBar(InfoBarSeverity.Warning, "尚未填写 API Key", "请先填写 DeepSeek API Key。");
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
