using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueScreenHelper.Services;
using Microsoft.UI.Xaml;

namespace BlueScreenHelper.Models;

public enum AIProvider
{
    OpenAI,
    Anthropic,
    // 保留旧值以兼容已经保存的配置；加载后统一迁移为 OpenAI 协议。
    Gemini,
    Custom
}

public static class DeepSeekEndpoints
{
    public const string OpenAI = "https://api.deepseek.com";
    public const string Anthropic = "https://api.deepseek.com/anthropic";

    public static AIProvider NormalizeProvider(AIProvider provider) =>
        provider == AIProvider.Anthropic ? AIProvider.Anthropic : AIProvider.OpenAI;

    public static string ForProvider(AIProvider provider) =>
        NormalizeProvider(provider) == AIProvider.Anthropic ? Anthropic : OpenAI;
}

public sealed class AIConfigItem
{
    public string Name { get; set; } = "";
    public AIProvider Provider { get; set; } = AIProvider.OpenAI;
    public string ApiBaseUrl { get; set; } = DeepSeekEndpoints.OpenAI;
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public string SystemPrompt { get; set; } = "";

    [JsonIgnore]
    public string ProviderDisplay => Provider switch
    {
        AIProvider.Anthropic => "DeepSeek（Anthropic 格式）",
        _ => "DeepSeek（OpenAI 格式）"
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

    public static readonly IReadOnlyList<AIConfigItem> Templates = new[]
    {
        new AIConfigItem
        {
            Name = "DeepSeek（OpenAI）",
            Provider = AIProvider.OpenAI,
            ApiBaseUrl = DeepSeekEndpoints.OpenAI,
            Model = "",
            SystemPrompt = DefaultPrompt
        },
        new AIConfigItem
        {
            Name = "DeepSeek（Anthropic）",
            Provider = AIProvider.Anthropic,
            ApiBaseUrl = DeepSeekEndpoints.Anthropic,
            Model = "",
            SystemPrompt = DefaultPrompt
        }
    };
}

public sealed class AppSettings
{
    private static readonly object SettingsIoSync = new();

    public bool WelcomeDismissed { get; set; }
    public List<AIConfigItem> AIConfigs { get; set; } = new();
    public string ActiveConfigName { get; set; } = "";

    [JsonIgnore]
    public AIConfigItem? ActiveConfig =>
        AIConfigs.FirstOrDefault(c => c.Name == ActiveConfigName)
        ?? AIConfigs.FirstOrDefault();

    public string ApiBaseUrl { get; set; } = DeepSeekEndpoints.OpenAI;
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public string SystemPrompt { get; set; } = AIPresets.DefaultPrompt;

    public static AppSettings Load()
    {
        lock (SettingsIoSync)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                if (File.Exists(ConfigFilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigFilePath))
                            ?? new AppSettings();
                    s.MigrateLegacySettings();
                    s.RestrictToDeepSeekEndpoints();
                    s.UpgradePrompts();
                    return s;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("读取设置文件失败", ex);
            }
        }
        return new AppSettings();
    }

    private void MigrateLegacySettings()
    {
        if (AIConfigs.Count > 0)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(Model))
        {
            AIConfigs.Add(new AIConfigItem
            {
                Name = "DeepSeek（OpenAI）",
                Provider = AIProvider.OpenAI,
                ApiBaseUrl = DeepSeekEndpoints.OpenAI,
                ApiKey = ApiKey,
                Model = Model,
                Temperature = Temperature,
                SystemPrompt = SystemPrompt
            });
            ActiveConfigName = "自定义";
            Save();
        }
    }

    private void RestrictToDeepSeekEndpoints()
    {
        var changed = false;
        foreach (var cfg in AIConfigs)
        {
            var provider = DeepSeekEndpoints.NormalizeProvider(cfg.Provider);
            var baseUrl = DeepSeekEndpoints.ForProvider(provider);
            if (cfg.Provider != provider)
            {
                cfg.Provider = provider;
                changed = true;
            }
            if (!string.Equals(cfg.ApiBaseUrl, baseUrl, StringComparison.Ordinal))
            {
                cfg.ApiBaseUrl = baseUrl;
                changed = true;
            }
        }
        if (changed)
        {
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

    public bool Save()
    {
        lock (SettingsIoSync)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                AtomicJsonFile.Write(ConfigFilePath, this);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("保存设置文件失败", ex);
                return false;
            }
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
    private StringBuilder? _pendingContent;
    private DateTime _lastRender = DateTime.MinValue;
    private UIElement? _rendered;

    public string Role { get; set; } = "user";

    public string Content
    {
        get => _pendingContent?.ToString() ?? _content;
        set
        {
            if (Content != value)
            {
                _content = value;
                _pendingContent = null;
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
        _pendingContent ??= new StringBuilder(_content, _content.Length + Math.Max(delta.Length, 256));
        _pendingContent.Append(delta);
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
        if (_pendingContent != null)
        {
            _content = _pendingContent.ToString();
            _pendingContent = null;
        }
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
    private static readonly object SessionIoSync = new();

    private sealed class SessionFile
    {
        public List<ChatSession> Sessions { get; set; } = new();
    }

    public static List<ChatSession> Load()
    {
        lock (SessionIoSync)
        {
            try
            {
                var path = AppSettings.SessionsFilePath;
                if (File.Exists(path))
                {
                    var file = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path));
                    return file?.Sessions ?? new List<ChatSession>();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("读取 AI 会话文件失败", ex);
            }
        }
        return new List<ChatSession>();
    }

    public static bool Save(List<ChatSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        lock (SessionIoSync)
        {
            try
            {
                var dir = Path.GetDirectoryName(AppSettings.SessionsFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                AtomicJsonFile.Write(AppSettings.SessionsFilePath, new SessionFile { Sessions = sessions });
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("保存 AI 会话文件失败", ex);
                return false;
            }
        }
    }
}

internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write<T>(string path, T value)
    {
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, Options));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
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
