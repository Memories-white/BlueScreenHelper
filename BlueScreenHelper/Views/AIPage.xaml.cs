using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private bool _refreshingConfigs;
    private bool _toolLogSubscribed;
    private readonly HashSet<string> _deletedSessionIds = new();

    public AIPage()
    {
        InitializeComponent();
        ChatList.ItemsSource = _messages;
        SessionList.ItemsSource = _sessions;
        ToolLogList.ItemsSource = AiToolLog.Entries;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SubscribeToolLog();
        UpdateToolLogState(false);
        RefreshAIConfigs();

        _sessions.Clear();
        foreach (var s in ChatSessionStore.Load().OrderByDescending(s => s.UpdatedAt))
        {
            _sessions.Add(s);
        }
        UpdateSessionEmptyState();

        var pending = AppState.PendingAI;
        if (pending != null && AIBox.SelectedItem is AIConfigItem)
        {
            AppState.PendingAI = null;
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
        else
        {
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

            if (pending != null)
            {
                AiBar.Title = "诊断内容已准备好";
                AiBar.Message = "请先在“设置”中保存一个可用的 AI 配置；返回此页后会自动开始诊断。";
                AiBar.IsOpen = true;
            }
        }
        UpdateConversationControls();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
        UnsubscribeToolLog();
    }

    private void RefreshAIConfigs()
    {
        _refreshingConfigs = true;
        try
        {
            AIBox.ItemsSource = null;
            AIBox.ItemsSource = AppState.Settings.AIConfigs;
            AIBox.SelectedItem = AppState.Settings.ActiveConfig;
        }
        finally
        {
            _refreshingConfigs = false;
        }
        AiBar.Title = "尚未保存任何 AI 配置";
        AiBar.Message = "请前往“设置”页面，从预设模板创建并保存一个 AI 服务配置。";
        AiBar.IsOpen = AppState.Settings.AIConfigs.Count == 0;
        UpdateSendButton();
    }

    private void AIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingConfigs)
        {
            return;
        }
        if (AIBox.SelectedItem is AIConfigItem config)
        {
            AppState.Settings.ActiveConfigName = config.Name;
            AppState.Settings.Save();
            AiBar.IsOpen = false;
        }
        UpdateSendButton();
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
        UpdateConversationControls();
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _current = new ChatSession();
        SessionList.SelectedItem = null;
        _messages.Clear();
        ChatEmpty.Visibility = Visibility.Visible;
        InputBox.Focus(FocusState.Programmatic);
        UpdateConversationControls();
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

        _deletedSessionIds.Add(session.Id);
        if (_current?.Id == session.Id)
        {
            _cts?.Cancel();
            _current = null;
            _messages.Clear();
        }
        _sessions.Remove(session);
        UpdateSessionEmptyState();
        ChatSessionStore.Save(_sessions.ToList());

        if (_current == null)
        {
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
        UpdateConversationControls();
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (!ctrl)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending)
        {
            _cts?.Cancel();
            return;
        }
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
        UpdateConversationControls();
        ScrollToBottom();
        await SendInternalAsync(null);
    }

    private async Task SendInternalAsync(string? systemPromptOverride)
    {
        if (_sending || AIBox.SelectedItem is not AIConfigItem config || _current == null)
        {
            return;
        }
        var session = _current;
        _sending = true;
        _cts = new CancellationTokenSource();
        SendButton.IsEnabled = true;
        SendButtonText.Text = "中止";
        SendIcon.Symbol = Symbol.Cancel;

        var snapshot = session.Messages.TakeLast(40).ToList();
        var assistant = new ChatMessage { Role = "assistant", Content = "正在思考..." };
        _messages.Add(assistant);
        session.Messages.Add(assistant);
        ScrollToBottom();
        var receivedContent = false;

        try
        {
            await _ai.ChatWithToolsAsync(config, snapshot, systemPromptOverride, delta =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (string.IsNullOrEmpty(delta))
                    {
                        return;
                    }
                    if (!receivedContent)
                    {
                        assistant.Content = delta;
                        receivedContent = true;
                    }
                    else
                    {
                        assistant.AppendContent(delta);
                    }
                    if (ReferenceEquals(_current, session))
                    {
                        ScrollToBottom();
                    }
                });
            }, tool =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    assistant.Content = $"正在自动采集系统信息：{tool.Name} ...";
                    receivedContent = false;
                    var sysMsg = new ChatMessage { Role = "system", Content = $"已自动采集：{tool.Name}（{tool.Action}）" };
                    session.Messages.Add(sysMsg);
                    if (ReferenceEquals(_current, session))
                    {
                        _messages.Add(sysMsg);
                        ScrollToBottom();
                    }
                });
            }, _cts.Token);
            if (!receivedContent)
            {
                assistant.Content = "AI 未返回可显示的诊断内容，请重试。";
            }
            assistant.FlushRender();
        }
        catch (OperationCanceledException)
        {
            assistant.Content = receivedContent
                ? assistant.Content + "\n\n（已停止生成）"
                : "（已停止生成）";
            assistant.FlushRender();
        }
        catch (Exception ex)
        {
            assistant.Content = $"[请求失败] {ex.Message}";
            assistant.FlushRender();
        }
        finally
        {
            SaveSession(session, config.Name);
            _sending = false;
            SendButtonText.Text = "发送";
            SendIcon.Symbol = Symbol.Send;
            _cts?.Dispose();
            _cts = null;
            UpdateConversationControls();
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _current.Messages.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "清空当前对话",
            Content = "当前对话中的所有消息都将被删除，此操作无法撤销。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _cts?.Cancel();
        var oldSession = _current;
        _deletedSessionIds.Add(oldSession.Id);
        _sessions.Remove(oldSession);
        _current = new ChatSession();
        SessionList.SelectedItem = null;
        _messages.Clear();
        ChatEmpty.Visibility = Visibility.Visible;
        ChatSessionStore.Save(_sessions.ToList());
        UpdateSessionEmptyState();
        UpdateConversationControls();
    }

    private void SaveSession(ChatSession session, string configName)
    {
        if (_deletedSessionIds.Contains(session.Id) || session.Messages.Count == 0)
        {
            ChatSessionStore.Save(_sessions.ToList());
            return;
        }

        session.ConfigName = configName;
        if (session.Title == "新对话")
        {
            session.RenameFromFirstMessage();
        }
        session.UpdatedAt = DateTime.Now;
        _sessions.Remove(session);
        _sessions.Insert(0, session);
        UpdateSessionEmptyState();
        if (ReferenceEquals(_current, session) && SessionList.SelectedItem == null)
        {
            SessionList.SelectedItem = session;
        }
        ChatSessionStore.Save(_sessions.ToList());
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSendButton();
    }

    private void UpdateConversationControls()
    {
        ClearButton.IsEnabled = _current?.Messages.Count > 0;
        UpdateSendButton();
    }

    private void UpdateSendButton()
    {
        SendButton.IsEnabled = _sending ||
            (AIBox.SelectedItem is AIConfigItem && !string.IsNullOrWhiteSpace(InputBox.Text));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateTo("settings");
    }

    private void SubscribeToolLog()
    {
        if (_toolLogSubscribed)
        {
            return;
        }
        AiToolLog.Entries.CollectionChanged += ToolLog_CollectionChanged;
        _toolLogSubscribed = true;
    }

    private void UnsubscribeToolLog()
    {
        if (!_toolLogSubscribed)
        {
            return;
        }
        AiToolLog.Entries.CollectionChanged -= ToolLog_CollectionChanged;
        _toolLogSubscribed = false;
    }

    private void ToolLog_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateToolLogState(true);
    }

    private void UpdateToolLogState(bool expand)
    {
        var hasEntries = AiToolLog.Entries.Count > 0;
        ToolLogList.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        ToolLogEmpty.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        if (expand && hasEntries)
        {
            ToolLogExpander.IsExpanded = true;
            ToolLogList.ScrollIntoView(AiToolLog.Entries[0]);
        }
    }

    private void ScrollToBottom()
    {
        if (_messages.Count > 0)
        {
            ChatList.ScrollIntoView(_messages[^1]);
        }
    }
}
