using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Ubar3;

/// <summary>
/// Top-of-screen, 60 px-tall desktop app bar. Reserves the top strip of the
/// primary monitor so maximized / snapped windows stop below the bar.
/// </summary>
public sealed partial class MainWindow : Window
{
    private AppBarManager? _appBar;

    public MainWindow()
    {
        Logger.Log("MainWindow constructor started.");
        InitializeComponent();

        AppWindow.Title = "Ubar 3.0";
        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));

        _appBar = new AppBarManager(this);
        Activated += OnFirstActivated;
        Closed += OnClosed;
        
        // Debug: global pointer pressed handler on window content
        if (this.Content is FrameworkElement content)
        {
            content.PointerPressed += (s, e) => Logger.Log($"[MainWindow] Content PointerPressed: {e.GetCurrentPoint(content).Position.X},{e.GetCurrentPoint(content).Position.Y}, Handled={e.Handled}");
            Logger.Log($"[MainWindow] Content PointerPressed handler attached");
        }
        else
        {
            // Content not ready yet, try after navigation
            RootFrame.Navigated += (s, e) =>
            {
                if (this.Content is FrameworkElement c)
                {
                    c.PointerPressed += (s, e) => Logger.Log($"[MainWindow] Content PointerPressed (Navigated): {e.GetCurrentPoint(c).Position.X},{e.GetCurrentPoint(c).Position.Y}, Handled={e.Handled}");
                    Logger.Log($"[MainWindow] Content PointerPressed handler attached (Navigated)");
                }
            };
        }
        
        Logger.Log("MainWindow constructor finished.");
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Logger.Log("MainWindow.OnFirstActivated triggered.");
        Activated -= OnFirstActivated;
        _appBar?.Register();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Logger.Log("MainWindow.Closed event triggered.");
        _appBar?.Dispose();
        _appBar = null;
    }
}
