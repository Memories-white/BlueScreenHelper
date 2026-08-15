using BlueScreenHelper.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueScreenHelper;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Type> _pages = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["dump"] = typeof(DumpPage),
        ["scanner"] = typeof(ScannerPage),
        ["ai"] = typeof(AIPage),
        ["settings"] = typeof(SettingsPage),
    };

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch
        {
        }
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
        }
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(_pages["dashboard"]);
    }

    public void NavigateTo(string tag)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi && nvi.Tag is string t && t == tag)
            {
                NavView.SelectedItem = nvi;
                break;
            }
        }
        if (_pages.TryGetValue(tag, out var type))
        {
            if (ContentFrame.CurrentSourcePageType == type)
            {
                if (ContentFrame.Content is DashboardPage dp)
                {
                    dp.ShowWelcomeBar();
                }
                return;
            }
            ContentFrame.Navigate(type);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            if (_pages.TryGetValue(tag, out var type))
            {
                if (ContentFrame.CurrentSourcePageType == type)
                {
                    if (ContentFrame.Content is DashboardPage dp)
                    {
                        dp.ShowWelcomeBar();
                    }
                    return;
                }
                ContentFrame.Navigate(type);
            }
        }
    }
}
