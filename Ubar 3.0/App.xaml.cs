using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

namespace Ubar3;

public partial class App : Application
{
    private Window? _window;
    public static MainWindow? MainWindowInstance { get; private set; }
    public static HardwareMonitoringService Metrics { get; private set; } = null!;
    public static ModuleManager Modules { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Logger.Log("Application initialized.");
        System.Diagnostics.Debug.WriteLine("DEBUG: App constructor called");
        UnhandledException += (s, e) =>
        {
            Logger.Log($"Unhandled Exception: {e.Message}\n{e.Exception}");
            System.Diagnostics.Debug.WriteLine($"DEBUG: Unhandled Exception: {e.Message}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Logger.Log($"AppDomain Unhandled Exception: {e.ExceptionObject}");
            System.Diagnostics.Debug.WriteLine($"DEBUG: AppDomain Unhandled Exception: {e.ExceptionObject}");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Logger.Log($"Unobserved Task Exception: {e.Exception.Message}\n{e.Exception}");
            System.Diagnostics.Debug.WriteLine($"DEBUG: Unobserved Task Exception: {e.Exception.Message}");
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Logger.Log("[App] OnLaunched ENTER");
        
        // 1. Create shared metrics service
        Metrics = new HardwareMonitoringService();

        // 2. Create module context
        var ctx = new ModuleContext { Metrics = Metrics };

        // 3. Create module manager
        Modules = new ModuleManager(ctx);
        ctx.Modules = Modules;

        // 4. Load launcher apps (needed before registering LauncherModules)
        var launcherApps = LauncherAppService.LoadApps() is { Count: > 0 } saved
            ? LauncherAppService.MergeWithDefaults(saved)
            : new System.Collections.Generic.List<LauncherApp>(LauncherAppDefaults.CreateDefaultApps());

        // 5. Register all modules
        Modules.RegisterModule(new LauncherModule());
        Modules.RegisterModule(new AppSwitcherModule());
        Modules.RegisterModule(new ActiveAppModule());
        Modules.RegisterModule(new CalendarModule());
        Modules.RegisterModule(new ClockModule());
        Modules.RegisterModule(new CpuModule());
        Modules.RegisterModule(new RamModule());
        Modules.RegisterModule(new BatteryModule());
        Modules.RegisterModule(new CpuTempModule());
        Modules.RegisterModule(new GpuTempModule());
        Modules.RegisterModule(new GpuModule());
        Modules.RegisterModule(new VramModule());
        Modules.RegisterModule(new WeatherModule());
        Modules.RegisterModule(new MusicModule());
        Modules.RegisterModule(new BluetoothModule());
        Modules.RegisterModule(new WifiModule());
        Modules.RegisterModule(new ExitModule());

        // 6. Initialize (creates views, enables visible modules)
        Modules.InitializeAll();

        _window = new MainWindow();
        MainWindowInstance = (MainWindow)_window;
        _window.Activate();

        // Overlay window that hosts flyouts beyond the bar's bounds.
        // DISABLED: the secondary window was blocking input to the bar.
        // Flyouts now show directly on the bar (clipped to bar bounds).
        // FlyoutHost.Initialize();
        _window.Activate();
    }
}