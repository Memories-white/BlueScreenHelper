using System.Collections.ObjectModel;
using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace BlueScreenHelper.Views;

public sealed partial class AIPage : Page
{
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly AIService _ai = new();
    private CancellationTokenSource? _cts;
    private bool _sending;

    public AIPage()
    {
        InitializeComponent();
        ChatList.ItemsSource = _messages;
        ModelBox.ItemsSource = new[]
        {
            "gpt-4o-mini", "gpt-4o", "gpt-4.1-mini", "deepseek-chat", "deepseek-reasoner",
            "glm-4-flash", "glm-4-plus", "moonshot-v1-8k", "qwen-plus", "qwen-turbo",
            "Meta-Llama-3.1-70B-Instruct", "Qwen/Qwen2.5-7B-Instruct", "llama3.1"
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ModelBox.Text = AppState.Settings.Model;

        var pending = AppState.PendingAI;
        AppState.PendingAI = null;
        if (pending != null)
        {
            var msg = new ChatMessage { Role = "user", Content = pending.UserMessage };
            _messages.Add(msg);
            ScrollToBottom();
            _ = SendInternalAsync(pending.SystemPrompt);
        }
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await SendAsync();
    }

    private async Task SendAsync()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _sending)
        {
            return;
        }
        InputBox.Text = "";
        _messages.Add(new ChatMessage { Role = "user", Content = text });
        ScrollToBottom();
        await SendInternalAsync(null);
    }

    private async Task SendInternalAsync(string? systemPromptOverride)
    {
        if (_sending)
        {
            return;
        }
        _sending = true;
        _cts = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;

        var snapshot = _messages.ToList();
        var assistant = new ChatMessage { Role = "assistant", Content = "正在思考..." };
        _messages.Add(assistant);
        ScrollToBottom();

        try
        {
            await _ai.ChatAsync(AppState.Settings, snapshot, systemPromptOverride, delta =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    assistant.Content += delta;
                    ScrollToBottom();
                });
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            assistant.Content += "\n\n（已停止生成）";
        }
        catch (Exception ex)
        {
            assistant.Content = $"[请求失败] {ex.Message}";
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
            StopButton.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;

            while (_messages.Count > 40)
            {
                _messages.RemoveAt(0);
            }

            if (!string.IsNullOrWhiteSpace(ModelBox.Text) &&
                !string.Equals(ModelBox.Text, AppState.Settings.Model, StringComparison.OrdinalIgnoreCase))
            {
                AppState.Settings.Model = ModelBox.Text.Trim();
                AppState.Settings.Save();
            }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _messages.Clear();
    }

    private void ScrollToBottom()
    {
        if (_messages.Count > 0)
        {
            ChatList.ScrollIntoView(_messages[^1]);
        }
    }
}