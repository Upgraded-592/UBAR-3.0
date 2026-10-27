using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using WinRT.Interop;
using UbarShared.Configuration;

namespace Ubar3;

public sealed partial class MainPage : Page
{
    private bool _isHiddenForFullscreen;
    private IntPtr _barHwnd = IntPtr.Zero;
    private readonly DispatcherTimer _fullscreenTimer;
    private bool _isBuildingModules;
    private readonly List<LauncherApp> _launcherApps = new();

    public MainPage()
    {
        InitializeComponent();
        Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] MainPage constructor called");

        LoadLauncherApps();

        Loaded += (_, _) =>
        {
            try
            {
                if (App.MainWindowInstance != null)
                {
                    _barHwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] [MainPage] Failed to get window handle: {ex.Message}");
            }
        };

        App.Modules.LayoutChanged += BuildModules;
        BuildModules();

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenTimer.Tick += (s, e) => CheckFullscreenAndToggleBar();
        _fullscreenTimer.Start();
    }

    private void BuildModules()
    {
        if (_isBuildingModules) return;
        _isBuildingModules = true;
        Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] BuildModules START, LeftSlot={LeftSlot.Children.Count}, CenterSlot={CenterSlot.Children.Count}, RightSlot={RightSlot.Children.Count}");

        try
        {
            var sorted = App.Modules.Slots
                .Where(s => s.IsVisible)
                .OrderBy(s => s.Order)
                .ToList();
            Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] BuildModules sorted count={sorted.Count}: {string.Join(", ", sorted.Select(s => $"{s.Module.Id}(O={s.Order},A={s.Alignment})"))}");

            LeftSlot.Children.Clear();
            CenterSlot.Children.Clear();
            RightSlot.Children.Clear();
            Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] BuildModules panels cleared");

            foreach (var slot in sorted)
            {
                var view = WrapModuleForLayout(slot);
                Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] BuildModules WrapModuleForLayout returned {view.GetType().Name} for {slot.Module.Id}");

                var panel = slot.Alignment switch
                {
                    ModuleAlignment.Left => LeftSlot,
                    ModuleAlignment.Center => CenterSlot,
                    _ => RightSlot,
                };
                panel.Children.Add(view);
                Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] BuildModules Added {slot.Module.Id} to {panel.Name}, panel children={panel.Children.Count}");
            }
            Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] BuildModules END, LeftSlot={LeftSlot.Children.Count}, CenterSlot={CenterSlot.Children.Count}, RightSlot={RightSlot.Children.Count}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] BuildModules EXCEPTION: {ex}");
            throw;
        }
        finally
        {
            _isBuildingModules = false;
        }
    }

    private FrameworkElement WrapModuleForLayout(ModuleSlot slot)
    {
        var view = slot.GetView();
        Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] [WrapModuleForLayout] {slot.Module.Id}: view={view.GetType().Name}, view.Parent={view.Parent?.GetType().Name ?? "null"}");

        if (view.Parent is Panel oldParent)
        {
            Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] [WrapModuleForLayout] {slot.Module.Id}: Removing view from old parent {oldParent.GetType().Name}");
            oldParent.Children.Remove(view);
        }
        view.ClearValue(Grid.ColumnProperty);
        view.ClearValue(Grid.RowProperty);
        view.ClearValue(Canvas.LeftProperty);
        view.ClearValue(Canvas.TopProperty);
        view.ClearValue(Canvas.ZIndexProperty);

        // Context menu — shown directly on the bar (clipped to bar bounds)
        var moduleMenu = BuildModuleContextMenu(slot);
        view.IsHitTestVisible = true;
        view.RightTapped += (s, e) =>
        {
            Logger.Log($"[MainPage] RightTapped on {slot.Module.Id}, showing context menu");
            moduleMenu.ShowAt(view, e.GetPosition(view));
            e.Handled = true;
        };
        
        return view;
    }

    private MenuFlyout BuildModuleContextMenu(ModuleSlot slot)
    {
        Logger.Log($"[MainPage] BuildModuleContextMenu for {slot.Module.Id}");
        var flyout = new MenuFlyout();

        flyout.Items.Add(new MenuFlyoutItem { Text = slot.Module.DisplayName, IsEnabled = false });
        flyout.Items.Add(new MenuFlyoutSeparator());

        var hide = new MenuFlyoutItem
        {
            Text = "Hide",
            Icon = new FontIcon { Glyph = "\uED1A", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") },
        };
        hide.Click += (s, e) => { Logger.Log($"[MainPage] Hide clicked for {slot.Module.Id}"); App.Modules.Disable(slot.Module.Id); };
        hide.PointerPressed += (s, e) => Logger.Log($"[MainPage] Hide PointerPressed for {slot.Module.Id}");
        flyout.Items.Add(hide);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var moveItem = new MenuFlyoutSubItem { Text = "Move to", Icon = new FontIcon { Glyph = "\uE75B", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") } };
        foreach (var zone in new[] { ModuleAlignment.Left, ModuleAlignment.Center, ModuleAlignment.Right })
        {
            var z = zone;
            var mi = new MenuFlyoutItem { Text = zone.ToString() };
            mi.Click += (s, e) => { Logger.Log($"[MainPage] MoveToZone clicked for {slot.Module.Id} to {z}"); App.Modules.MoveToZone(slot.Module.Id, z); };
            mi.PointerPressed += (s, e) => Logger.Log($"[MainPage] MoveToZone PointerPressed for {slot.Module.Id} to {z}");
            moveItem.Items.Add(mi);
        }
        flyout.Items.Add(moveItem);

        var reorder = new MenuFlyoutSubItem { Text = "Reorder", Icon = new FontIcon { Glyph = "\uE8CB", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") } };
        var shiftLeft = new MenuFlyoutItem { Text = "Shift Left" };
        shiftLeft.Click += (s, e) => { Logger.Log($"[MainPage] ShiftLeft clicked for {slot.Module.Id}"); App.Modules.ShiftLeft(slot.Module.Id); };
        shiftLeft.PointerPressed += (s, e) => Logger.Log($"[MainPage] ShiftLeft PointerPressed for {slot.Module.Id}");
        reorder.Items.Add(shiftLeft);
        var shiftRight = new MenuFlyoutItem { Text = "Shift Right" };
        shiftRight.Click += (s, e) => { Logger.Log($"[MainPage] ShiftRight clicked for {slot.Module.Id}"); App.Modules.ShiftRight(slot.Module.Id); };
        shiftRight.PointerPressed += (s, e) => Logger.Log($"[MainPage] ShiftRight PointerPressed for {slot.Module.Id}");
        reorder.Items.Add(shiftRight);
        flyout.Items.Add(reorder);

        if (slot.Module is LauncherModule)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var cfg = new MenuFlyoutItem { Text = "Configure Apps", Icon = new FontIcon { Glyph = "\uE713", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") } };
            cfg.Click += (s, e) => ShowConfigureAppsDialog();
            flyout.Items.Add(cfg);
        }

        return flyout;
    }

    private void PopulateContextFlyout()
    {
        BarContextFlyout.Items.Clear();

        BarContextFlyout.Items.Add(new MenuFlyoutItem { Text = "Modules", IsEnabled = false });
        BarContextFlyout.Items.Add(new MenuFlyoutSeparator());

        foreach (var slot in App.Modules.Slots)
        {
            var s = slot;
            var item = new ToggleMenuFlyoutItem
            {
                Text = slot.Module.DisplayName,
                IsChecked = slot.IsVisible,
                Icon = new FontIcon { Glyph = slot.Module.Glyph, FontFamily = ModuleGlyphFont.For(slot.Module.Glyph) },
            };
            item.Click += (_, _) =>
            {
                if (s.IsVisible) App.Modules.Disable(s.Module.Id);
                else App.Modules.Enable(s.Module.Id);
            };
            BarContextFlyout.Items.Add(item);
        }

        BarContextFlyout.Items.Add(new MenuFlyoutSeparator());

        var resetItem = new MenuFlyoutItem { Text = "Reset Layout to Default" };
        resetItem.Click += (s, ev) => ResetLayout();
        BarContextFlyout.Items.Add(resetItem);

        var showAll = new MenuFlyoutItem { Text = "Show All Modules" };
        showAll.Click += (s, ev) =>
        {
            foreach (var slot in App.Modules.Slots)
                if (!slot.IsVisible) App.Modules.Enable(slot.Module.Id);
        };
        BarContextFlyout.Items.Add(showAll);

        BarContextFlyout.Items.Add(new MenuFlyoutSeparator());

        var settingsItem = new MenuFlyoutItem
        {
            Text = "Settings",
            Icon = new FontIcon { Glyph = "\uE713", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }
        };
        settingsItem.Click += (s, ev) => ShowSettingsWindow();
        BarContextFlyout.Items.Add(settingsItem);
    }

    private void RootBar_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Module right-clicks set e.Handled in their own RightTapped handlers
        // (see WrapModuleForLayout), so this only fires on empty bar space.
        Logger.Log($"[{DateTime.Now:HH:mm:ss}] RootBar_RightTapped called on empty space");
        System.Diagnostics.Debug.WriteLine($"[MainPage] RootBar_RightTapped called on empty space");
        PopulateContextFlyout();
        var point = e.GetPosition(RootBar);
        // Show directly on the bar (clipped to bar bounds, but at least it works)
        BarContextFlyout.ShowAt(RootBar, point);
        e.Handled = true;
    }

    private void ResetLayout()
    {
        foreach (var slot in App.Modules.Slots)
        {
            slot.Alignment = slot.Module.DefaultAlignment;
            slot.Order = slot.Module.DefaultOrder;
            slot.IsVisible = true;
            if (!slot.IsEnabled && !slot.IsFaulted) slot.Enable();
        }
        BuildModules();
        App.Modules.ReorderCommit();
    }

    private void ShowSettingsWindow()
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.Activate();
    }

    private void LoadLauncherApps()
    {
        var saved = LauncherAppService.LoadApps();
        if (saved != null && saved.Count > 0)
        {
            _launcherApps.Clear();
            _launcherApps.AddRange(LauncherAppService.MergeWithDefaults(saved));
        }
        else
        {
            _launcherApps.Clear();
            _launcherApps.AddRange(LauncherAppDefaults.CreateDefaultApps());
        }
    }

private static readonly (string Glyph, string Name)[] CommonGlyphs = new[]
{
    // E700-E7FF range (official Fluent Icons)
    ("\uE700", "GlobalNavButton"), ("\uE701", "Wifi"), ("\uE702", "Bluetooth"),
    ("\uE704", "InternetSharing"), ("\uE705", "VPN"), ("\uE706", "Brightness"),
    ("\uE707", "MapPin"), ("\uE708", "QuietHours"), ("\uE709", "Airplane"),
    ("\uE70A", "Tablet"), ("\uE70B", "QuickNote"), ("\uE70C", "RememberedDevice"),
    ("\uE70D", "ChevronDown"), ("\uE70E", "ChevronUp"), ("\uE70F", "Edit"),
    ("\uE710", "Add"), ("\uE711", "Cancel"), ("\uE712", "More"),
    ("\uE713", "Settings"), ("\uE714", "Video"), ("\uE715", "Mail"),
    ("\uE716", "People"), ("\uE717", "Phone"), ("\uE718", "Pin"),
    ("\uE719", "Shop"), ("\uE71A", "Stop"), ("\uE71B", "Link"),
    ("\uE71C", "Filter"), ("\uE71D", "AllApps"), ("\uE71E", "Zoom"),
    ("\uE71F", "ZoomOut"), ("\uE720", "Microphone"), ("\uE721", "Search"),
    ("\uE722", "Camera"), ("\uE723", "Attach"), ("\uE724", "Send"),
    ("\uE725", "SendFill"), ("\uE726", "WalkSolid"), ("\uE727", "InPrivate"),
    ("\uE728", "FavoriteList"), ("\uE729", "PageSolid"), ("\uE72A", "Forward"),
    ("\uE72B", "Back"), ("\uE72C", "Refresh"), ("\uE72D", "Share"),
    ("\uE72E", "Lock"), ("\uE730", "ReportHacked"), ("\uE731", "EMI"),
    ("\uE733", "Blocked"), ("\uE734", "FavoriteStar"), ("\uE735", "FavoriteStarFill"),
    ("\uE736", "ReadingMode"), ("\uE737", "Favicon"), ("\uE738", "Remove"),
    ("\uE739", "Checkbox"), ("\uE73A", "CheckboxComposite"), ("\uE73B", "CheckboxFill"),
    ("\uE73C", "CheckboxIndeterminate"), ("\uE73D", "CheckboxCompositeReversed"),
    ("\uE73E", "CheckMark"), ("\uE73F", "BackToWindow"), ("\uE740", "FullScreen"),
    ("\uE741", "ResizeTouchLarger"), ("\uE742", "ResizeTouchSmaller"),
    ("\uE743", "ResizeMouseSmall"), ("\uE744", "ResizeMouseMedium"),
    ("\uE745", "ResizeMouseWide"), ("\uE746", "ResizeMouseTall"),
    ("\uE747", "ResizeMouseLarge"), ("\uE748", "SwitchUser"),
    ("\uE749", "Print"), ("\uE74A", "Up"), ("\uE74B", "Down"),
    ("\uE74C", "OEM"), ("\uE74D", "Delete"), ("\uE74E", "Save"),
    ("\uE74F", "Mute"), ("\uE750", "BackSpaceQWERTY"), ("\uE751", "ReturnKey"),
    ("\uE752", "UpArrowShiftKey"), ("\uE753", "Cloud"), ("\uE754", "Flashlight"),
    ("\uE755", "RotationLock"), ("\uE756", "CommandPrompt"), ("\uE759", "SIPMove"),
    ("\uE75A", "SIPUndock"), ("\uE75B", "SIPRedock"), ("\uE75C", "EraseTool"),
    ("\uE75D", "UnderscoreSpace"), ("\uE75E", "GripperTool"), ("\uE75F", "Dialpad"),
    ("\uE760", "PageLeft"), ("\uE761", "PageRight"), ("\uE762", "MultiSelect"),
    ("\uE763", "KeyboardLeftHanded"), ("\uE764", "KeyboardRightHanded"),
    ("\uE765", "KeyboardClassic"), ("\uE766", "KeyboardSplit"),
    ("\uE767", "Volume"), ("\uE768", "Play"), ("\uE769", "Pause"),
    ("\uE76B", "ChevronLeft"), ("\uE76C", "ChevronRight"),
    ("\uE76D", "InkingTool"), ("\uE76E", "Emoji2"), ("\uE76F", "GripperBarHorizontal"),
    ("\uE770", "System"), ("\uE771", "Personalize"), ("\uE772", "Devices"),
    ("\uE773", "SearchAndApps"), ("\uE774", "Globe"), ("\uE775", "TimeLanguage"),
    ("\uE776", "EaseOfAccess"), ("\uE777", "UpdateRestore"), ("\uE778", "HangUp"),
    ("\uE779", "ContactInfo"), ("\uE77A", "Unpin"), ("\uE77B", "Contact"),
    ("\uE77C", "Memo"), ("\uE77E", "IncomingCall"), ("\uE77F", "Paste"),
    ("\uE780", "PhoneBook"), ("\uE781", "LEDLight"), ("\uE783", "Error"),
    ("\uE784", "GripperBarVertical"), ("\uE785", "Unlock"), ("\uE786", "Slideshow"),
    ("\uE787", "Calendar"), ("\uE788", "GripperResize"), ("\uE789", "Megaphone"),
    ("\uE78A", "Trim"), ("\uE78B", "NewWindow"), ("\uE78C", "SaveLocal"),
    ("\uE790", "Color"), ("\uE791", "DataSense"), ("\uE792", "SaveAs"),
    ("\uE793", "Light"), ("\uE794", "Effects"), ("\uE799", "AspectRatio"),
    ("\uE7A1", "Contrast"), ("\uE7A5", "DataSenseBar"), ("\uE7A6", "Redo"),
    ("\uE7A7", "Undo"), ("\uE7A8", "Crop"), ("\uE7AA", "PhotoCollection"),
    ("\uE7AC", "OpenWith"), ("\uE7AD", "Rotate"), ("\uE7B3", "RedEye"),
    ("\uE7B5", "SetlockScreen"), ("\uE7B7", "MapPin2"), ("\uE7B8", "Package"),
    ("\uE7BA", "Warning"), ("\uE7BC", "ReadingList"), ("\uE7BE", "Education"),
    ("\uE7BF", "ShoppingCart"), ("\uE7C0", "Train"), ("\uE7C1", "Flag"),
    ("\uE7C2", "Move"), ("\uE7C3", "Page"), ("\uE7C4", "TaskView"),
    ("\uE7C5", "BrowsePhotos"), ("\uE7C6", "HalfStarLeft"), ("\uE7C7", "HalfStarRight"),
    ("\uE7C8", "Record"), ("\uE7C9", "TouchPointer"), ("\uE7DE", "LangJPN"),
    ("\uE7E3", "Ferry"), ("\uE7E6", "Highlight"), ("\uE7E7", "ActionCenterNotification"),
    ("\uE7E8", "PowerButton"), ("\uE7EA", "ResizeTouchNarrower"),
    ("\uE7EB", "ResizeTouchShorter"), ("\uE7EC", "DrivingMode"),
    ("\uE7ED", "RingerSilent"), ("\uE7EE", "OtherUser"), ("\uE7EF", "Admin"),
    ("\uE7F0", "CC"), ("\uE7F1", "SDCard"), ("\uE7F2", "CallForwarding"),
    ("\uE7F3", "SettingsDisplaySound"), ("\uE7F4", "TVMonitor"),
    ("\uE7F5", "Speakers"), ("\uE7F6", "Headphone"), ("\uE7F7", "DeviceLaptopPic"),
    ("\uE7F8", "DeviceLaptopNoPic"), ("\uE7F9", "DeviceMonitorRightPic"),
    ("\uE7FA", "DeviceMonitorLeftPic"), ("\uE7FB", "DeviceMonitorNoPic"),
    ("\uE7FC", "Game"), ("\uE7FD", "HorizontalTabKey"),

    // E800-E8FF range
    ("\uE8B7", "Folder"), ("\uE8AF", "Remote"),

    // E900-E9FF range
    ("\uE917", "Clock"), ("\uE91D", "Location"), ("\uE945", "LightningBolt"),
    ("\uE95D", "Projector"),

    // EA00-ECFF range
    ("\uEA18", "Shield"), ("\uEA37", "List"), ("\uEA80", "Lightbulb"),
    ("\uEA89", "CalendarSolid"), ("\uEA8A", "HomeSolid"), ("\uEA90", "PDF"),
    ("\uEB3B", "GenericApp"), ("\uEB3C", "Design"), ("\uEB41", "Website"),
    ("\uEB42", "Drop"), ("\uEB44", "Radar"), ("\uEB50", "Reminder"),
    ("\uEB51", "Heart"), ("\uEB52", "HeartFill"),

    // EE00-EEFF range
    ("\uEEA0", "RAM"), ("\uEEA1", "CPU"),

    // F000-F5FF range
    ("\uF120", "TaskManagerApp"), ("\uF8B0", "SettingsSolid"),
};

private async void ShowConfigureAppsDialog()
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialogWindow = new Window
        {
            Title = "Configure App Launcher",
            Content = new Grid(), // placeholder
        };
        dialogWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        dialogWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1560, 600));

var panel = new StackPanel { Spacing = 12, Width = 1500, Margin = new Thickness(24) };

        var tabView = new TabView { Height = 550 };
        
        // Tab 1: Launcher Apps
        var appsTab = new TabViewItem { Header = "Launcher Apps" };
        var appsPanel = new StackPanel { Spacing = 12 };
        appsPanel.Children.Add(new TextBlock
        {
            Text = "Apps in launcher (uncheck to hide):",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var appsList = new ListView { SelectionMode = ListViewSelectionMode.None, Height = 400 };
        var rows = new List<(CheckBox cb, TextBox name, ComboBox glyph, TextBox imagePath, Button browseBtn, TextBox cmd, TextBox args, LauncherApp app)>();

        void Rebuild()
        {
            appsList.Items.Clear();
            rows.Clear();
            foreach (var app in _launcherApps)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                var cb = new CheckBox { IsChecked = app.IsVisible, VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBox { Text = app.DisplayName, PlaceholderText = "Name", Width = 120 };

                var glyphCombo = new ComboBox { Width = 70, MinWidth = 70 };
                foreach (var (g, n) in CommonGlyphs)
                {
                    glyphCombo.Items.Add(new ComboBoxItem
                    {
                        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                            new FontIcon { Glyph = g, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 14, Width = 24 },
                            new TextBlock { Text = n, VerticalAlignment = VerticalAlignment.Center }
                        }},
                        Tag = g
                    });
                }
                int selectedIndex = Array.FindIndex(CommonGlyphs, g => g.Glyph == app.Glyph);
                glyphCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                glyphCombo.SelectionChanged += (s, e) =>
                {
                    if (s is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string g)
                    {
                        // Glyph will be read from combo on save
                    }
                };

                var imagePathBox = new TextBox 
                { 
                    Text = app.ImagePath, 
                    PlaceholderText = "Image path (.ico, .png, .jpg...)", 
                    Width = 350 
                };
                var browseBtn = new Button 
                { 
                    Content = new FontIcon { Glyph = "\uE774", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }, 
                    Padding = new Thickness(8, 4, 8, 4),
                };
                ToolTipService.SetToolTip(browseBtn, "Browse for image");
                browseBtn.Click += async (s, e) =>
                {
                    Logger.Log($"[ConfigDialog] Browse button clicked for {app.DisplayName}");
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    picker.FileTypeFilter.Add(".ico");
                    picker.FileTypeFilter.Add(".png");
                    picker.FileTypeFilter.Add(".jpg");
                    picker.FileTypeFilter.Add(".jpeg");
                    picker.FileTypeFilter.Add(".bmp");
                    picker.FileTypeFilter.Add(".gif");
                    picker.FileTypeFilter.Add(".tiff");
                    picker.FileTypeFilter.Add(".webp");
                    picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
                    picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                    
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(dialogWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                    
                    var file = await picker.PickSingleFileAsync();
                    Logger.Log($"[ConfigDialog] File picked: {file?.Path ?? "null"}, FileType: {file?.FileType ?? "null"}");
                    if (file != null)
                    {
                        // Copy to app's local state folder for persistence
                        try
                        {
                            var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                            var destFile = await file.CopyAsync(localFolder, file.Name, Windows.Storage.NameCollisionOption.GenerateUniqueName);
                            imagePathBox.Text = destFile.Name; // Store relative name
                        }
                        catch
                        {
                            imagePathBox.Text = file.Path; // Fallback to full path
                        }
                    }
                };
                ToolTipService.SetToolTip(browseBtn, "Browse for image");

                var cmd = new TextBox { Text = app.Command, PlaceholderText = "Command", Width = 300 };
                var args = new TextBox { Text = app.Arguments, PlaceholderText = "Args", Width = 120 };
                var cur = app;
                var rem = new Button { Content = new FontIcon { Glyph = "\uE74D", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") }, Background = null, BorderThickness = new Thickness(0) };
                ToolTipService.SetToolTip(rem, "Delete this app");
                rem.Click += (s, e) => { Logger.Log($"[ConfigDialog] Remove button clicked for {app.DisplayName}"); _launcherApps.Remove(cur); Rebuild(); };
                row.Children.Add(cb);
                row.Children.Add(name);
                row.Children.Add(glyphCombo);
                row.Children.Add(imagePathBox);
                row.Children.Add(browseBtn);
                row.Children.Add(cmd);
                row.Children.Add(args);
                row.Children.Add(rem);
                appsList.Items.Add(row);
                rows.Add((cb, name, glyphCombo, imagePathBox, browseBtn, cmd, args, app));
            }
        }
        Rebuild();
        appsPanel.Children.Add(appsList);

        var addBtn = new Button();
        var addContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        addContent.Children.Add(new FontIcon { Glyph = "\uE710", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") });
        addContent.Children.Add(new TextBlock { Text = "Add App", VerticalAlignment = VerticalAlignment.Center });
        addBtn.Content = addContent;
        addBtn.Click += (s, e) =>
        {
            _launcherApps.Add(new LauncherApp { Id = "app" + Guid.NewGuid().ToString("N")[..8], DisplayName = "New App", Glyph = "\uE713", Command = "", IsVisible = true });
            Rebuild();
        };
        appsPanel.Children.Add(addBtn);

        appsTab.Content = appsPanel;

        // Tab 2: Modules
        var modulesTab = new TabViewItem { Header = "Modules" };
        var modulesPanel = new StackPanel { Spacing = 12 };
        modulesPanel.Children.Add(new TextBlock
        {
            Text = "Configure module visibility, position, and order:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var modulesList = new ListView { SelectionMode = ListViewSelectionMode.None, Height = 400 };
        var moduleRows = new List<(CheckBox cb, TextBlock name, ComboBox align, TextBox order, ModuleSlot slot)>();

        void RebuildModules()
        {
            modulesList.Items.Clear();
            moduleRows.Clear();

            foreach (var slot in App.Modules.Slots)
            {
                var module = slot.Module;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 6, 0, 6) };

                var cb = new CheckBox { IsChecked = slot.IsVisible, VerticalAlignment = VerticalAlignment.Center, Width = 40 };
                cb.Click += (s, e) =>
                {
                    if (cb.IsChecked == true) App.Modules.Enable(module.Id);
                    else App.Modules.Disable(module.Id);
                };

                var name = new TextBlock { Text = $"{module.DisplayName} ({module.Id})", Width = 200, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };

                var alignCombo = new ComboBox { Width = 100, VerticalAlignment = VerticalAlignment.Center };
                alignCombo.Items.Add(new ComboBoxItem { Content = "Left", Tag = ModuleAlignment.Left });
                alignCombo.Items.Add(new ComboBoxItem { Content = "Center", Tag = ModuleAlignment.Center });
                alignCombo.Items.Add(new ComboBoxItem { Content = "Right", Tag = ModuleAlignment.Right });
                alignCombo.SelectedIndex = (int)slot.Alignment;
                alignCombo.SelectionChanged += (s, e) =>
                {
                    if (s is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is ModuleAlignment align)
                    {
                        App.Modules.Reorder(module.Id, align, slot.Order);
                        RebuildModules();
                    }
                };

                var orderBox = new TextBox { Text = slot.Order.ToString(), Width = 60, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = "Order" };
                orderBox.TextChanged += (s, e) =>
                {
                    if (s is TextBox tb && int.TryParse(tb.Text, out int order))
                    {
                        App.Modules.Reorder(module.Id, slot.Alignment, order);
                        RebuildModules();
                    }
                };

                row.Children.Add(cb);
                row.Children.Add(name);
                row.Children.Add(alignCombo);
                row.Children.Add(orderBox);
                modulesList.Items.Add(row);
                moduleRows.Add((cb, name, alignCombo, orderBox, slot));
            }
        }
        RebuildModules();
        modulesPanel.Children.Add(modulesList);

        var resetModulesBtn = new Button { Content = "Reset Layout to Default", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35)) };
        resetModulesBtn.Click += (s, e) =>
        {
            App.Modules.ReorderCommit();
            RebuildModules();
        };
        modulesPanel.Children.Add(resetModulesBtn);

        modulesTab.Content = modulesPanel;

        tabView.TabItems.Add(appsTab);
        tabView.TabItems.Add(modulesTab);

        // Buttons at bottom (outside tabs)
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var saveBtn = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancelBtn = new Button { Content = "Cancel" };
        var deleteAllBtn = new Button { Content = "Delete All", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35)) };
        buttonPanel.Children.Add(deleteAllBtn);
        buttonPanel.Children.Add(saveBtn);
        buttonPanel.Children.Add(cancelBtn);

        var mainPanel = new StackPanel { Spacing = 12 };
        mainPanel.Children.Add(tabView);
        mainPanel.Children.Add(buttonPanel);

        dialogWindow.Content = mainPanel;

        saveBtn.Click += (s, e) =>
        {
            foreach (var (cb, name, glyphCombo, imagePathBox, _, cmd, args, app) in rows)
            {
                app.IsVisible = cb.IsChecked == true;
                if (!string.IsNullOrWhiteSpace(name.Text)) app.DisplayName = name.Text;
                if (glyphCombo.SelectedItem is ComboBoxItem item && item.Tag is string g)
                    app.Glyph = g;
                if (!string.IsNullOrWhiteSpace(imagePathBox.Text))
                    app.ImagePath = imagePathBox.Text;
                else
                    app.ImagePath = string.Empty;
                app.Command = cmd.Text;
                app.Arguments = args.Text;
            }
            LauncherAppService.SaveApps(_launcherApps);

            var launcherSlot = App.Modules.Slots.FirstOrDefault(s => s.Module is LauncherModule);
            if (launcherSlot?.Module is LauncherModule lm)
            {
                lm.RefreshButtons();
            }

            BuildModules();
            tcs.SetResult(true);
            dialogWindow.Close();
        };

        cancelBtn.Click += (s, e) =>
        {
            LoadLauncherApps();
            tcs.SetResult(false);
            dialogWindow.Close();
        };

        deleteAllBtn.Click += async (s, e) =>
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "Delete All Apps",
                Content = "Are you sure you want to delete all launcher apps? This cannot be undone.",
                PrimaryButtonText = "Delete All",
                CloseButtonText = "Cancel",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = dialogWindow.Content.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                _launcherApps.Clear();
                Rebuild();
            }
        };

        dialogWindow.Closed += (s, e) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.SetResult(false);
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        var dialogHwnd = WinRT.Interop.WindowNative.GetWindowHandle(dialogWindow);
        Win32Interop.SetWindowPos(dialogHwnd, hwnd, 0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);

        dialogWindow.Activate();

        await tcs.Task;
    }

    private void CheckFullscreenAndToggleBar()
    {
        try
        {
            IntPtr fgHwnd = Win32Interop.GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero || fgHwnd == _barHwnd) { SetBarHidden(false); return; }

            if (!Win32Interop.GetWindowRect(fgHwnd, out Win32Interop.RECT fgRect)) { SetBarHidden(false); return; }

            IntPtr monitor = Win32Interop.MonitorFromWindow(fgHwnd, Win32Interop.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) { SetBarHidden(false); return; }

            var mi = new Win32Interop.MONITORINFO { cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>() };
            if (!Win32Interop.GetMonitorInfo(monitor, ref mi)) { SetBarHidden(false); return; }

            bool fs = fgRect.Left <= mi.rcMonitor.Left && fgRect.Top <= mi.rcMonitor.Top
                   && fgRect.Right >= mi.rcMonitor.Right && fgRect.Bottom >= mi.rcMonitor.Bottom;
            SetBarHidden(fs);
        }
        catch (Exception ex) { Logger.Log($"[{DateTime.Now:HH:mm:ss.fff}] CheckFullscreen error: {ex.Message}"); }
    }

    private void SetBarHidden(bool hide)
    {
        if (hide == _isHiddenForFullscreen) return;
        _isHiddenForFullscreen = hide;
        if (_barHwnd == IntPtr.Zero) return;
        if (hide)
            Win32Interop.SetWindowPos(_barHwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_HIDEWINDOW);
        else
            Win32Interop.SetWindowPos(_barHwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW | Win32Interop.SWP_NOOWNERZORDER);
    }
}