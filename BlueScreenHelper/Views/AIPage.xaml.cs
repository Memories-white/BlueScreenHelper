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
    private readonly ObservableCollection<ChatSession> _sessions = new();
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly AIService _ai = new();
    private ChatSession? _current;
    private CancellationTokenSource? _cts;
    private bool _sending;

    public AIPage()
    {
        InitializeComponent();
        ChatList.ItemsSource = _messages;
        SessionList.ItemsSource = _sessions;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshAIConfigs();

        _sessions.Clear();
        foreach (var s in ChatSessionStore.Load().OrderByDescending(s => s.UpdatedAt))
        {
            _sessions.Add(s);
        }
        UpdateSessionEmptyState();

        var pending = AppState.PendingAI;
        AppState.PendingAI = null;
        if (pending != null)
        {
            var session = new ChatSession();
            _current = session;
            SessionList.SelectedItem = null;
            var msg = new ChatMessage { Role = "user", Content = pending.UserMessage };
            _messages.Add(msg);
            session.Messages.Add(msg);
            ChatEmpty.Visibility = Visibility.Collapsed;
            ScrollToBottom();
            _ = SendInternalAsync(pending.SystemPrompt);
        }
        else if (_sessions.Count > 0)
        {
            SessionList.SelectedItem = _sessions[0];
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
    }

    private void RefreshAIConfigs()
    {
        AIBox.ItemsSource = null;
        AIBox.ItemsSource = AppState.Settings.AIConfigs;
        AIBox.SelectedItem = AppState.Settings.ActiveConfig;
        AiBar.IsOpen = AppState.Settings.AIConfigs.Count == 0;
        SendButton.IsEnabled = AppState.Settings.AIConfigs.Count > 0 && !_sending;
    }

    private void UpdateSessionEmptyState()
    {
        SessionEmpty.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not ChatSession session || session == _current)
        {
            return;
        }
        _cts?.Cancel();
        _current = session;
        _messages.Clear();
        foreach (var m in session.Messages)
        {
            _messages.Add(m);
        }
        ChatEmpty.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_messages.Count > 0)
        {
            ScrollToBottom();
        }
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _current = new ChatSession();
        SessionList.SelectedItem = null;
        _messages.Clear();
        ChatEmpty.Visibility = Visibility.Visible;
    }

    private async void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string id)
        {
            return;
        }
        var session = _sessions.FirstOrDefault(s => s.Id == id);
        if (session == null)
        {
            return;
        }

        var dlg = new ContentDialog
        {
            Title = "删除对话",
            Content = $"确定要删除对话“{session.Title}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _sessions.Remove(session);
        UpdateSessionEmptyState();
        ChatSessionStore.Save(_sessions.ToList());

        if (_current?.Id == session.Id)
        {
            _cts?.Cancel();
            if (_sessions.Count > 0)
            {
                SessionList.SelectedItem = _sessions[0];
            }
            else
            {
                _current = new ChatSession();
                _messages.Clear();
                ChatEmpty.Visibility = Visibility.Visible;
            }
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
        if (AIBox.SelectedItem is not AIConfigItem)
        {
            AiBar.IsOpen = true;
            return;
        }
        InputBox.Text = "";
        _current ??= new ChatSession();
        var msg = new ChatMessage { Role = "user", Content = text };
        _messages.Add(msg);
        _current.Messages.Add(msg);
        ChatEmpty.Visibility = Visibility.Collapsed;
        ScrollToBottom();
        await SendInternalAsync(null);
    }

    private async Task SendInternalAsync(string? systemPromptOverride)
    {
        if (_sending || AIBox.SelectedItem is not AIConfigItem config)
        {
            return;
        }
        _sending = true;
        _cts = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;

        var snapshot = _current!.Messages.TakeLast(40).ToList();
        var assistant = new ChatMessage { Role = "assistant", Content = "正在思考..." };
        _messages.Add(assistant);
        _current.Messages.Add(assistant);
        ScrollToBottom();

        try
        {
            await _ai.ChatAsync(config, snapshot, systemPromptOverride, delta =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    assistant.AppendContent(delta);
                    ScrollToBottom();
                });
            }, _cts.Token);
            assistant.FlushRender();

            _current.ConfigName = config.Name;
            if (_current.Title == "新对话")
            {
                _current.RenameFromFirstMessage();
            }
            if (!_sessions.Contains(_current))
            {
                _sessions.Insert(0, _current);
                UpdateSessionEmptyState();
            }
            _current.UpdatedAt = DateTime.Now;
            _sessions.Remove(_current);
            _sessions.Insert(0, _current);
            UpdateSessionEmptyState();
            if (SessionList.SelectedItem == null)
            {
                SessionList.SelectedItem = _current;
            }
            ChatSessionStore.Save(_sessions.ToList());
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
            SendButton.IsEnabled = AppState.Settings.AIConfigs.Count > 0;
            StopButton.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }
        _cts?.Cancel();
        _messages.Clear();
        _current.Messages.Clear();
        ChatEmpty.Visibility = Visibility.Visible;
        ChatSessionStore.Save(_sessions.ToList());
    }

    private void ScrollToBottom()
    {
        if (_messages.Count > 0)
        {
            ChatList.ScrollIntoView(_messages[^1]);
        }
    }
}
