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
        "Qwen/Qwen2.5-7B-Instruct", "Meta-Llama-3.1-70B-Instruct", "llama3.1"
    };

    public SettingsPage()
    {
        InitializeComponent();
        ModelBox.ItemsSource = ModelPresets;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        BaseUrlBox.Text = s.ApiBaseUrl;
        ApiKeyBox.Password = s.ApiKey;
        ModelBox.Text = s.Model;
        TempSlider.Value = s.Temperature;
        PromptBox.Text = s.SystemPrompt;
        SaveBar.IsOpen = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        s.ApiBaseUrl = BaseUrlBox.Text.Trim();
        s.ApiKey = ApiKeyBox.Password.Trim();
        s.Model = ModelBox.Text.Trim();
        s.Temperature = TempSlider.Value;
        s.SystemPrompt = PromptBox.Text;
        s.Save();
        SaveBar.Severity = InfoBarSeverity.Success;
        SaveBar.Title = "配置已保存";
        SaveBar.Message = $"配置文件位置：{AppSettings.ConfigFilePath}";
        SaveBar.IsOpen = true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        s.ApiBaseUrl = BaseUrlBox.Text.Trim();
        s.ApiKey = ApiKeyBox.Password.Trim();
        s.Model = ModelBox.Text.Trim();
        s.Temperature = TempSlider.Value;
        s.SystemPrompt = PromptBox.Text;

        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            SaveBar.Severity = InfoBarSeverity.Warning;
            SaveBar.Title = "尚未填写 API Key";
            SaveBar.Message = "请先在 API Key 中输入密钥，或选择支持无需密钥的本地服务（如 Ollama）。";
            SaveBar.IsOpen = true;
            return;
        }

        TestButton.IsEnabled = false;
        TestRing.IsActive = true;
        SaveBar.IsOpen = false;

        try
        {
            var ai = new AIService();
            var result = await ai.TestConnectionAsync(s);
            SaveBar.Severity = InfoBarSeverity.Success;
            SaveBar.Title = "连接成功";
            SaveBar.Message = $"接口与模型可用，返回：{result}";
        }
        catch (Exception ex)
        {
            SaveBar.Severity = InfoBarSeverity.Error;
            SaveBar.Title = "连接失败";
            SaveBar.Message = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestRing.IsActive = false;
            SaveBar.IsOpen = true;
        }
    }
}