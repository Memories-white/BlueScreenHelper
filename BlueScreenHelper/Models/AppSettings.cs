using System.ComponentModel;

namespace BlueScreenHelper.Models;

public sealed class AppSettings
{
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.3;
    public string SystemPrompt { get; set; } =
        "你是一位经验丰富的 Windows 系统故障诊断专家，擅长蓝屏(BSOD)崩溃转储分析。" +
        "请根据用户提供的数据，按以下结构用中文回答：\n" +
        "1. 问题定位：最可能的故障原因\n" +
        "2. 详细分析：解释崩溃机制与相关证据\n" +
        "3. 解决方案：按优先级列出可操作步骤（包含命令、工具）\n" +
        "4. 预防建议\n" +
        "若数据不足，请明确指出并建议用户补充哪些信息。";

    public static AppSettings Load()
    {
        try
        {
            var dir = SettingsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            if (File.Exists(path))
            {
                return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
        }
        catch
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = SettingsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    public static string ConfigFilePath =>
        Path.Combine(SettingsDir, "settings.json");

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueScreenHelper");
}

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _content = "";

    public string Role { get; set; } = "user";

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            }
        }
    }

    public string DisplayName => Role == "user" ? "你" : "AI 助手";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AIContext
{
    public required string UserMessage { get; set; }
    public string? SystemPrompt { get; set; }
}

public static class AppState
{
    public static AIContext? PendingAI { get; set; }
    public static AppSettings Settings { get; private set; } = AppSettings.Load();

    public static void ReloadSettings()
    {
        Settings = AppSettings.Load();
    }
}