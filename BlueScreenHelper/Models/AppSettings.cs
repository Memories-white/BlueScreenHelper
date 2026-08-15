using System.ComponentModel;
using System.Text.Json.Serialization;
using BlueScreenHelper.Services;
using Microsoft.UI.Xaml;

namespace BlueScreenHelper.Models;

public enum AIProvider
{
    OpenAI,
    Anthropic,
    Gemini,
    Custom
}

public sealed class AIConfigItem
{
    public string Name { get; set; } = "";
    public AIProvider Provider { get; set; } = AIProvider.OpenAI;
    public string ApiBaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public string SystemPrompt { get; set; } = "";

    [JsonIgnore]
    public string ProviderDisplay => Provider switch
    {
        AIProvider.Anthropic => "Anthropic 协议",
        AIProvider.Gemini => "Gemini 协议",
        AIProvider.Custom => "自定义（OpenAI 兼容，如 Ollama）",
        _ => "OpenAI 协议"
    };
}

public static class AIPresets
{
    public const string LegacyDefaultPrompt =
        "你是一位经验丰富的 Windows 系统故障诊断专家，擅长蓝屏(BSOD)崩溃转储分析。" +
        "请根据用户提供的数据，按以下结构用中文回答：\n" +
        "1. 问题定位：最可能的故障原因\n" +
        "2. 详细分析：解释崩溃机制与相关证据\n" +
        "3. 解决方案：按优先级列出可操作步骤（包含命令、工具）\n" +
        "4. 预防建议\n" +
        "若数据不足，请明确指出并建议用户补充哪些信息。";

    public const string DefaultPrompt =
        "你是一位经验丰富的 Windows 系统故障诊断专家，擅长蓝屏(BSOD)崩溃分析与系统问题排查。" +
        "用户正在使用“蓝屏诊断助手”——该工具已经内置转储文件(.dmp)解析、系统事件日志读取与系统健康扫描能力，相关数据会直接提供给用户。\n" +
        "重要约束：\n" +
        "- 禁止建议用户安装或使用 WinDbg、Windows SDK、WindbgX、BlueScreenView 等外部调试工具去分析转储文件（本工具已代为完成解析）；\n" +
        "- 不要建议用户手动查看事件查看器（Event Viewer）去查崩溃记录（本工具已读取）；\n" +
        "- 只给出直接、可操作的系统修复方案，例如驱动回滚/更新、运行 sfc /scannow、DISM、卸载冲突软件、硬件检测（内存/硬盘）等。\n" +
        "回答要求：\n" +
        "1. 问题定位：最可能的故障原因\n" +
        "2. 详细分析：解释崩溃机制与相关证据\n" +
        "3. 解决方案：按优先级列出可操作步骤（包含命令、工具）\n" +
        "4. 预防建议\n" +
        "请使用 Markdown 格式组织回答（标题、加粗、列表、代码块），内容用中文。\n" +
        "若数据不足，请明确指出并建议用户补充哪些信息。";

    public static IReadOnlyList<AIConfigItem> Templates => new[]
    {
        new AIConfigItem
        {
            Name = "OpenAI",
            Provider = AIProvider.OpenAI,
            ApiBaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            SystemPrompt = DefaultPrompt
        },
        new AIConfigItem
        {
            Name = "Anthropic Claude",
            Provider = AIProvider.Anthropic,
            ApiBaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-sonnet-4-5",
            SystemPrompt = DefaultPrompt
        },
        new AIConfigItem
        {
            Name = "Google Gemini",
            Provider = AIProvider.Gemini,
            ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.0-flash",
            SystemPrompt = DefaultPrompt
        },
        new AIConfigItem
        {
            Name = "自定义",
            Provider = AIProvider.Custom,
            ApiBaseUrl = "http://localhost:11434/v1",
            Model = "llama3.1",
            SystemPrompt = DefaultPrompt
        }
    };
}

public sealed class AppSettings
{
    public bool WelcomeDismissed { get; set; }
    public List<AIConfigItem> AIConfigs { get; set; } = new();
    public string ActiveConfigName { get; set; } = "";

    [JsonIgnore]
    public AIConfigItem? ActiveConfig =>
        AIConfigs.FirstOrDefault(c => c.Name == ActiveConfigName)
        ?? AIConfigs.FirstOrDefault();

    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.3;
    public string SystemPrompt { get; set; } = AIPresets.DefaultPrompt;

    public static AppSettings Load()
    {
        try
        {
            var dir = SettingsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            if (File.Exists(path))
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                s.MigrateLegacySettings();
                s.UpgradePrompts();
                return s;
            }
        }
        catch
        {
        }
        return new AppSettings();
    }

    private void MigrateLegacySettings()
    {
        if (AIConfigs.Count > 0)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(ApiKey) || !string.Equals(Model, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase))
        {
            AIConfigs.Add(new AIConfigItem
            {
                Name = "自定义",
                Provider = AIProvider.Custom,
                ApiBaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrl) ? "https://api.openai.com/v1" : ApiBaseUrl,
                ApiKey = ApiKey,
                Model = Model,
                Temperature = Temperature,
                SystemPrompt = SystemPrompt
            });
            ActiveConfigName = "自定义";
            Save();
        }
    }

    private void UpgradePrompts()
    {
        var changed = false;
        foreach (var cfg in AIConfigs)
        {
            var isLegacyDefault = !string.IsNullOrWhiteSpace(cfg.SystemPrompt)
                && !cfg.SystemPrompt.Contains("蓝屏诊断助手")
                && cfg.SystemPrompt.Contains("BSOD")
                && cfg.SystemPrompt.Contains("请根据用户提供的数据");
            if (string.IsNullOrWhiteSpace(cfg.SystemPrompt) || isLegacyDefault)
            {
                cfg.SystemPrompt = AIPresets.DefaultPrompt;
                changed = true;
            }
        }
        if (changed)
        {
            Save();
        }
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

    public static string SessionsFilePath =>
        Path.Combine(SettingsDir, "sessions.json");

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueScreenHelper");
}

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _content = "";
    private DateTime _lastRender = DateTime.MinValue;
    private UIElement? _rendered;

    public string Role { get; set; } = "user";

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                RebuildRender();
            }
        }
    }

    [JsonIgnore]
    public UIElement? Rendered => _rendered;

    public string DisplayName => Role switch
    {
        "user" => "你",
        "system" => "系统",
        _ => "AI 助手"
    };

    public void AppendContent(string delta)
    {
        _content += delta;
        var now = DateTime.UtcNow;
        if (now - _lastRender > TimeSpan.FromMilliseconds(200))
        {
            _lastRender = now;
            RebuildRender();
        }
    }

    public void FlushRender()
    {
        RebuildRender();
    }

    private void RebuildRender()
    {
        _rendered = MarkdownRenderer.Render(_content);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rendered)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public string ConfigName { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public void RenameFromFirstMessage()
    {
        var first = Messages.FirstOrDefault(m => m.Role == "user");
        if (first != null && !string.IsNullOrWhiteSpace(first.Content))
        {
            var text = first.Content.Replace("\r", " ").Replace("\n", " ").Trim();
            Title = text.Length > 20 ? text[..20] + "..." : text;
        }
    }
}

public static class ChatSessionStore
{
    private sealed class SessionFile
    {
        public List<ChatSession> Sessions { get; set; } = new();
    }

    public static List<ChatSession> Load()
    {
        try
        {
            var path = AppSettings.SessionsFilePath;
            if (File.Exists(path))
            {
                var file = System.Text.Json.JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path));
                return file?.Sessions ?? new List<ChatSession>();
            }
        }
        catch
        {
        }
        return new List<ChatSession>();
    }

    public static void Save(List<ChatSession> sessions)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppSettings.SessionsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var file = new SessionFile { Sessions = sessions };
            File.WriteAllText(AppSettings.SessionsFilePath,
                System.Text.Json.JsonSerializer.Serialize(file, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
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
