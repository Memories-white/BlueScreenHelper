using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueScreenHelper.Models;

namespace BlueScreenHelper.Services;

public sealed class AIService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly Regex ToolCallRegex = new(@"\[\[TOOL:([A-Za-z0-9_]+)\]\]", RegexOptions.Compiled);
    private static readonly string[] ModelIdProperties = ["id", "name", "model"];

    public async Task<string> ChatAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        if (config == null)
        {
            throw new InvalidOperationException("尚未选择 AI 配置，请先在“设置”页面保存一个 AI 服务。");
        }
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("该 AI 配置尚未填写 API Key，请先在“设置”页面填写。");
        }

        return config.Provider == AIProvider.Anthropic
            ? await ChatAnthropicAsync(config, history, systemPromptOverride, onDelta, ct)
            : await ChatOpenAIAsync(config, history, systemPromptOverride, onDelta, ct);
    }

    /// <summary>
    /// 带系统工具调用能力的对话：模型通过 [[TOOL:key]] 请求采集本机系统数据，自动执行并把结果回传给模型继续分析。
    /// </summary>
    public async Task<string> ChatWithToolsAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, Action<AiToolDef>? onToolStart, CancellationToken ct)
    {
        var work = new List<ChatMessage>(history.Count + 10);
        foreach (var message in history)
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                work.Add(message);
            }
        }

        var mergedSystem = (string.IsNullOrWhiteSpace(systemPromptOverride)
            ? config.SystemPrompt
            : systemPromptOverride) + AiSystemTools.Instructions;

        for (var round = 0; round < 5; round++)
        {
            var reply = await ChatAsync(config, work, mergedSystem, onDelta, ct);

            var m = ToolCallRegex.Match(reply);
            if (!m.Success)
            {
                return reply;
            }

            var tool = AiSystemTools.Find(m.Groups[1].Value);
            work.Add(new ChatMessage { Role = "assistant", Content = reply });

            if (tool == null)
            {
                work.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "[系统采集结果] 请求了未知工具，请忽略该请求，基于已有信息继续分析，不要再次请求工具。"
                });
                continue;
            }

            onToolStart?.Invoke(tool);

            string result;
            var success = true;
            try
            {
                result = await Task.Run(tool.Execute, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                success = false;
                result = $"采集失败：{ex.Message}";
            }

            AiToolLog.Add(tool.Name, tool.Action, success,
                success ? FirstLine(result) : result);

            work.Add(new ChatMessage
            {
                Role = "user",
                Content = $"[系统采集结果: {tool.Name}]\n{Truncate(result, 6000)}"
            });
        }

        throw new InvalidOperationException("工具调用轮次过多，已自动停止。请重试。");
    }

    private static string FirstLine(string text)
    {
        var t = text.Trim();
        var idx = t.IndexOf('\n');
        return idx > 0 ? t[..idx] : t;
    }

    public async Task<string> TestConnectionAsync(AIConfigItem config)
    {
        var history = new List<ChatMessage>
        {
            new() { Role = "user", Content = "请仅回复两个字符：OK" }
        };
        return await ChatAsync(config, history, "你是一个连接测试助手，收到任何消息只回复 OK。", null, CancellationToken.None);
    }

    /// <summary>
    /// 通过 DeepSeek OpenAI 兼容接口查询模型列表。
    /// </summary>
    public async Task<List<string>> ListModelsAsync(AIConfigItem config, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DeepSeekEndpoints.OpenAI}/models");
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"查询模型列表失败 ({(int)response.StatusCode})：{Truncate(err, 300)}");
        }

        await using var json = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var models = new List<string>();

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            AddModels(data, models);
        }
        else if (root.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            AddModels(arr, models);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            AddModels(root, models);
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException("该接口未返回任何模型，可能不支持模型列表查询，请手动输入模型名称。");
        }
        return models.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddModels(JsonElement items, List<string> models)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddModel(item.GetString(), models);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var propertyName in ModelIdProperties)
            {
                if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    AddModel(value.GetString(), models);
                    break;
                }
            }
        }
    }

    private static void AddModel(string? model, List<string> models)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        models.Add(model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? model["models/".Length..]
            : model);
    }

    // ---------- DeepSeek OpenAI 兼容协议 ----------

    private async Task<string> ChatOpenAIAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        var messages = new List<object>();
        var sys = string.IsNullOrWhiteSpace(systemPromptOverride) ? config.SystemPrompt : systemPromptOverride;
        if (!string.IsNullOrWhiteSpace(sys))
        {
            messages.Add(new { role = "system", content = sys });
        }
        foreach (var m in history)
        {
            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                messages.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
            }
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(config.Model) ? "deepseek-flash" : config.Model,
            messages,
            stream = true,
            temperature = config.Temperature
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildOpenAIUri());
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
        request.Content = JsonContent.Create(body);
        return await SendStreamingAsync(request, ExtractOpenAiDelta, onDelta, ct).ConfigureAwait(false);
    }

    // ---------- Anthropic 协议 ----------

    private async Task<string> ChatAnthropicAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        var sys = string.IsNullOrWhiteSpace(systemPromptOverride) ? config.SystemPrompt : systemPromptOverride;
        var messages = new List<object>();
        foreach (var m in history)
        {
            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                messages.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
            }
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(config.Model) ? "deepseek-flash" : config.Model,
            max_tokens = 4096,
            system = sys ?? "",
            messages,
            stream = true,
            temperature = config.Temperature
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildAnthropicUri());
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(body);
        return await SendStreamingAsync(request, ExtractAnthropicDelta, onDelta, ct).ConfigureAwait(false);
    }

    // ---------- 辅助 ----------

    private static async Task<string> SendStreamingAsync(HttpRequestMessage request,
        Func<JsonElement, string?> extractDelta, Action<string>? onDelta, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"API 请求失败 ({(int)response.StatusCode})：{Truncate(error, 400)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
        var pendingDeltas = onDelta == null ? null : new StringBuilder();
        var nextDeltaFlush = Environment.TickCount64 + 50;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line.AsSpan(5).Trim();
            if (data.SequenceEqual("[DONE]"))
            {
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(data.ToString());
                var delta = extractDelta(document.RootElement);
                if (string.IsNullOrEmpty(delta))
                {
                    continue;
                }

                result.Append(delta);
                if (pendingDeltas != null)
                {
                    pendingDeltas.Append(delta);
                    if (Environment.TickCount64 >= nextDeltaFlush)
                    {
                        onDelta!(pendingDeltas.ToString());
                        pendingDeltas.Clear();
                        nextDeltaFlush = Environment.TickCount64 + 50;
                    }
                }
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (pendingDeltas?.Length > 0)
        {
            onDelta!(pendingDeltas.ToString());
        }

        return FinishResult(result);
    }

    private static string? ExtractOpenAiDelta(JsonElement root)
    {
        var delta = root.GetProperty("choices")[0].GetProperty("delta");
        return delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }

    private static string? ExtractAnthropicDelta(JsonElement root)
    {
        return root.TryGetProperty("type", out var type) && type.GetString() == "content_block_delta" &&
               root.TryGetProperty("delta", out var delta) &&
               delta.TryGetProperty("type", out var deltaType) && deltaType.GetString() == "text_delta" &&
               delta.TryGetProperty("text", out var text)
            ? text.GetString()
            : null;
    }

    private static string FinishResult(StringBuilder sb)
    {
        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("API 未返回有效内容，请检查模型名称、接口地址或 API Key。");
        }
        return result;
    }

    private static Uri BuildOpenAIUri()
    {
        return new Uri($"{DeepSeekEndpoints.OpenAI}/chat/completions");
    }

    private static Uri BuildAnthropicUri()
    {
        return new Uri($"{DeepSeekEndpoints.Anthropic}/v1/messages");
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..max] + "...";
    }
}
