using BlueScreenHelper.Services;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace BlueScreenHelper;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public static IntPtr MainWindowHandle =>
        MainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(MainWindow);

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLogger.Init();
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
