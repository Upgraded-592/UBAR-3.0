using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using Windows.System;

namespace Ubar3;

/// <summary>
/// WiFi module for UBAR - manages WiFi networks with connect/disconnect/forget/add hidden network.
/// </summary>
public sealed class WifiModule : IUbarModule
{
    public string Id => "wifi";
    public string DisplayName => "WiFi";
    public string Glyph => "\uE701"; // Segoe Fluent Icons: Wifi
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 10;

    private readonly ObservableCollection<WifiNetworkViewModel> _networks = new();
    private WiFiAdapter? _wifiAdapter;
    private string? _wifiAdapterDeviceId;
    private DeviceWatcher? _watcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;
    private FrameworkElement? _root;
    private Button? _toggleButton;
    private TextBlock? _statusText;
    private FontIcon? _glyphIcon;
    private Flyout? _flyout;
    private ListView? _networkList;
    private Border? _flyoutBorder;
    private bool _isFlyoutOpen;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private ConnectionProfile? _currentConnection;

    public FrameworkElement Initialize(ModuleContext context)
    {
        _glyphIcon = new FontIcon
        {
            Glyph = "\uE701",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            Foreground = GetAccentBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _statusText = new TextBlock
        {
            Text = "Loading...",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        _toggleButton = new Button
        {
            Padding = new Thickness(10, 4, 10, 4),
            Background = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumBrush"],
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { _glyphIcon, _statusText }
            },
        };
        ToolTipService.SetToolTip(_toggleButton, "Click to open WiFi networks");
        _toggleButton.Click += OnToggleClick;

        _root = new Border
        {
            Child = _toggleButton,
            CornerRadius = new CornerRadius(16),
            Background = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumBrush"],
        };

        // Build flyout content
        BuildFlyout();

        // Initialize WiFi adapter and start scanning
        _ = InitializeWifiAsync();

        // Periodic refresh for connection state changes
        _refreshTimer = _uiDispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += async (_, _) => await RefreshNetworkStatesAsync();
        _refreshTimer.Start();

        return _root;
    }

    public void OnEnabled()
    {
        _refreshTimer?.Start();
    }

    public void OnDisabled()
    {
        _refreshTimer?.Stop();
    }

    public void Dispose()
    {
        _refreshTimer?.Stop();
        _watcher?.Stop();
        _watcher = null;
        _wifiAdapter = null;
    }

    private async Task InitializeWifiAsync()
    {
        try
        {
            // Request access to WiFi adapter
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed)
            {
                UpdateUI("No Access", "\uE703"); // Wifi disabled/blocked
                return;
            }

            // Find WiFi adapter
            var deviceInfo = await DeviceInformation.FindAllAsync(WiFiAdapter.GetDeviceSelector());
            if (deviceInfo.Count == 0)
            {
                UpdateUI("No Adapter", "\uE703");
                return;
            }

            _wifiAdapter = await WiFiAdapter.FromIdAsync(deviceInfo[0].Id);
            if (_wifiAdapter == null)
            {
                UpdateUI("No Adapter", "\uE703");
                return;
            }

            _wifiAdapterDeviceId = deviceInfo[0].Id;

            _wifiAdapter.AvailableNetworksChanged += OnAvailableNetworksChanged;

            // Get current connection profile
            _currentConnection = NetworkInformation.GetInternetConnectionProfile();

            // Start scanning
            await _wifiAdapter.ScanAsync();

            // Start device watcher for network changes
            StartNetworkWatcher();

            await UpdateUIAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[WifiModule] Init failed: {ex}");
            UpdateUI("Error", "\uE703");
        }
    }

    private void StartNetworkWatcher()
    {
        try
        {
            var selector = WiFiAdapter.GetDeviceSelector();
            _watcher = DeviceInformation.CreateWatcher(selector);
            _watcher.Added += OnAdapterAdded;
            _watcher.Removed += OnAdapterRemoved;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Logger.Log($"[WifiModule] Watcher start failed: {ex}");
        }
    }

    private void OnAdapterAdded(DeviceWatcher sender, DeviceInformation deviceInfo)
    {
        _uiDispatcher.TryEnqueue(async () =>
        {
            if (_wifiAdapter == null)
            {
                _wifiAdapter = await WiFiAdapter.FromIdAsync(deviceInfo.Id);
                if (_wifiAdapter != null)
                {
                    _wifiAdapterDeviceId = deviceInfo.Id;
                    _wifiAdapter.AvailableNetworksChanged += OnAvailableNetworksChanged;
                    await _wifiAdapter.ScanAsync();
                    await UpdateUIAsync();
                }
            }
        });
    }

    private void OnAdapterRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        _uiDispatcher.TryEnqueue(() =>
        {
            if (_wifiAdapterDeviceId == update.Id)
            {
                _wifiAdapter = null;
                _wifiAdapterDeviceId = null;
                UpdateUI("No Adapter", "\uE703");
            }
        });
    }

    private void OnAvailableNetworksChanged(WiFiAdapter sender, object args)
    {
        _uiDispatcher.TryEnqueue(async () => await RefreshNetworksAsync());
    }

    private async Task RefreshNetworksAsync()
    {
        if (_wifiAdapter == null) return;

        try
        {
            var report = _wifiAdapter.NetworkReport;
            var availableNetworks = report.AvailableNetworks;

            // Update current connection
            _currentConnection = NetworkInformation.GetInternetConnectionProfile();

            var seenIds = new HashSet<string>();

            foreach (var network in availableNetworks)
            {
                var id = network.Ssid + "_" + network.Bssid;
                seenIds.Add(id);

                var existing = _networks.FirstOrDefault(n => n.NetworkId == id);
                if (existing != null)
                {
                    existing.Update(network, _currentConnection);
                }
                else
                {
                    var vm = new WifiNetworkViewModel(network, _currentConnection);
                    _networks.Add(vm);
                }
            }

            // Remove networks that are no longer available
            for (int i = _networks.Count - 1; i >= 0; i--)
            {
                if (!seenIds.Contains(_networks[i].NetworkId))
                {
                    // Keep known/profiled networks even if not currently visible
                    if (!_networks[i].IsKnownNetwork)
                    {
                        _networks.RemoveAt(i);
                    }
                }
            }

            // Sort: connected first, then known, then by signal strength
            var sorted = _networks
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.IsKnownNetwork)
                .ThenByDescending(n => n.SignalStrength)
                .ToList();

            _networks.Clear();
            foreach (var n in sorted)
                _networks.Add(n);

            RefreshNetworkList();
            await UpdateUIAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[WifiModule] Refresh networks failed: {ex}");
        }
    }

    private async Task RefreshNetworkStatesAsync()
    {
        _currentConnection = NetworkInformation.GetInternetConnectionProfile();
        foreach (var network in _networks)
        {
            network.UpdateConnectionState(_currentConnection);
        }
        RefreshNetworkList();
        await UpdateUIAsync();
    }

    private void RefreshNetworkList()
    {
        if (_networkList != null)
        {
            _networkList.ItemsSource = null;
            _networkList.ItemsSource = _networks;
        }
    }

    private void BuildFlyout()
    {
        _networkList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = CreateNetworkItemTemplate(),
            ItemsSource = _networks,
            MaxHeight = 400,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(12, 12, 12, 8),
            Children =
            {
                new FontIcon { Glyph = "\uE701", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 },
                new TextBlock { Text = "WiFi Networks", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            }
        };

        var radioToggle = new ToggleSwitch
        {
            Header = "WiFi",
            IsOn = _wifiAdapter != null,
            Margin = new Thickness(12, 0, 12, 8),
            MinWidth = 300,
        };
        radioToggle.Toggled += async (_, _) => await ToggleRadioAsync(radioToggle.IsOn);

        var scanButton = new Button
        {
            Content = "Scan for Networks",
            Margin = new Thickness(12, 0, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        scanButton.Click += async (_, _) => await ScanAsync();

        var addHiddenButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE710", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14 },
                    new TextBlock { Text = "Add Hidden Network", VerticalAlignment = VerticalAlignment.Center }
                }
            },
            Margin = new Thickness(12, 0, 12, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        addHiddenButton.Click += async (_, _) => await AddHiddenNetworkAsync();

        var content = new StackPanel
        {
            Children = { header, radioToggle, scanButton, addHiddenButton, _networkList }
        };

        _flyoutBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Padding = new Thickness(0),
            Child = content,
            Width = 340,
        };

        _flyout = new Flyout
        {
            Content = _flyoutBorder,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };
        _flyout.Closed += (_, _) => _isFlyoutOpen = false;
    }

    private DataTemplate CreateNetworkItemTemplate()
    {
        var xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
              xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
    <Grid Padding='12,8' ColumnSpacing='12'>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width='Auto'/>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        
        <!-- Signal Strength Icon -->
        <FontIcon Glyph='{Binding SignalGlyph}' 
                  FontFamily='Segoe Fluent Icons' 
                  FontSize='20' 
                  Foreground='{ThemeResource AccentFillColorDefaultBrush}'
                  VerticalAlignment='Center'/>
        
        <!-- Network Info -->
        <StackPanel Grid.Column='1' Spacing='2' VerticalAlignment='Center'>
            <TextBlock Text='{Binding DisplayName}' 
                       FontWeight='SemiBold' 
                       FontSize='13'
                       TextTrimming='CharacterEllipsis'/>
            <TextBlock Text='{Binding StatusText}' 
                       FontSize='11' 
                       Opacity='0.7'
                       Foreground='{Binding StatusColor}'/>
        </StackPanel>
        
        <!-- Actions -->
        <StackPanel Grid.Column='2' Orientation='Horizontal' Spacing='4' VerticalAlignment='Center'>
            <Button Content='{Binding ConnectButtonText}'
                    FontSize='11'
                    Padding='8,2'
                    MinWidth='0'
                    Height='28'
                    Visibility='{Binding ShowConnectButton}'
                    Click='OnConnectClick'
                    Tag='{Binding}'/>
            <Button Content='&#xE74D;' 
                    FontFamily='Segoe Fluent Icons'
                    FontSize='12'
                    Padding='6,2'
                    MinWidth='0'
                    Height='28'
                    ToolTipService.ToolTip='Forget network'
                    Click='OnForgetClick'
                    Tag='{Binding}'
                    Visibility='{Binding IsKnownNetwork}'
                    Foreground='{ThemeResource SystemFillColorCriticalBrush}'/>
        </StackPanel>
    </Grid>
</DataTemplate>";

        return (DataTemplate)XamlReader.Load(xaml);
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_flyout == null || _root == null) return;

        // Build flyout (shown directly on the bar)
        BuildFlyout();

        _root.PointerPressed += (s, e) =>
        {
            if (_flyout == null || _root == null) return;
            if (_isFlyoutOpen)
            {
                _flyout.Hide();
                _isFlyoutOpen = false;
            }
            else
            {
                _flyout.ShowAt(_root);
                _isFlyoutOpen = true;
            }
        };
    }

    private async Task ToggleRadioAsync(bool turnOn)
    {
        // WiFi radio control requires specific permissions and is limited in UWP
        // We can only request scan or show settings
        if (turnOn)
        {
            await ScanAsync();
        }
        else
        {
            // Cannot programmatically turn off WiFi radio in UWP
            // Show settings instead
            await Launcher.LaunchUriAsync(new Uri("ms-settings:network-wifi"));
        }
    }

    private async Task ScanAsync()
    {
        if (_wifiAdapter != null)
        {
            try
            {
                await _wifiAdapter.ScanAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[WifiModule] Scan failed: {ex}");
            }
        }
    }

    private async Task AddHiddenNetworkAsync()
    {
        var ssidBox = new TextBox
        {
            PlaceholderText = "Network name (SSID)",
            MaxLength = 32,
            Margin = new Thickness(0, 8, 0, 8),
        };

        var inputPanel = new StackPanel
        {
            Spacing = 8,
            Children = { ssidBox }
        };

        var dialog = new ContentDialog
        {
            Title = "Add Hidden Network",
            Content = inputPanel,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _root?.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(ssidBox.Text))
        {
            await ConnectToHiddenNetworkAsync(ssidBox.Text.Trim());
        }
    }

    private async Task ConnectToHiddenNetworkAsync(string ssid)
    {
        if (_wifiAdapter == null) return;

        try
        {
            // Launch WiFi settings for hidden network connection
            await Launcher.LaunchUriAsync(new Uri("ms-settings:network-wifi"));
        }
        catch (Exception ex)
        {
            Logger.Log($"[WifiModule] Connect hidden failed: {ex}");
            await ShowErrorDialog($"Connection failed: {ex.Message}");
        }
    }

    private Brush GetAccentBrush()
    {
        try
        {
            return (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }
        catch
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
        }
    }

    private void UpdateUI(string text, string glyph)
    {
        if (_statusText != null) _statusText.Text = text;
        if (_glyphIcon != null) _glyphIcon.Glyph = glyph;
    }

    private async Task UpdateUIAsync()
    {
        if (_wifiAdapter == null)
        {
            UpdateUI("No Adapter", "\uE703");
            return;
        }

        var isConnected = _currentConnection?.IsWlanConnectionProfile == true;
        var connectedNetwork = _networks.FirstOrDefault(n => n.IsConnected);
        var ssid = connectedNetwork?.DisplayName ?? _currentConnection?.WlanConnectionProfileDetails?.GetConnectedSsid() ?? "";

        if (isConnected && !string.IsNullOrEmpty(ssid))
        {
            UpdateUI($"Connected: {ssid}", "\uE701");
        }
        else if (_wifiAdapter.NetworkReport.AvailableNetworks.Count > 0)
        {
            UpdateUI("Available", "\uE701");
        }
        else
        {
            UpdateUI("No Networks", "\uE701");
        }
    }

    // Event handlers for flyout buttons (defined in DataTemplate)
    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is WifiNetworkViewModel vm)
        {
            await vm.ToggleConnectionAsync(_wifiAdapter);
            RefreshNetworkList();
            await UpdateUIAsync();
        }
    }

    private async void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is WifiNetworkViewModel vm)
        {
            var result = await ShowConfirmDialog($"Forget \"{vm.DisplayName}\"? This will remove the saved network profile.");
            if (result)
            {
                await vm.ForgetAsync();
                _networks.Remove(vm);
                RefreshNetworkList();
                await UpdateUIAsync();
            }
        }
    }

    private async Task<bool> ShowConfirmDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Confirm",
            Content = message,
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _root?.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task ShowErrorDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = _root?.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}

/// <summary>
/// View model for a WiFi network in the UI.
/// </summary>
public sealed class WifiNetworkViewModel
{
    public string NetworkId { get; }
    public string DisplayName { get; private set; }
    public string Ssid { get; }
    public string Bssid { get; }
    public int SignalStrength { get; private set; } // 0-100
    public string SignalGlyph { get; private set; } = "\uE701";
    public WiFiNetworkKind NetworkKind { get; }
    public bool IsConnected { get; private set; }
    public bool IsKnownNetwork { get; private set; }
    public string SecuritySettings { get; private set; } = string.Empty;
    
    public string StatusText => IsConnected 
        ? "Connected" 
        : (IsKnownNetwork ? "Saved" : "Available");
    
    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";
    public Visibility ShowConnectButton => Visibility.Visible;
    
    public Brush StatusColor => IsConnected
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16))
        : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private WiFiAvailableNetwork? _network;
    private ConnectionProfile? _currentConnection;

    public WifiNetworkViewModel(WiFiAvailableNetwork network, ConnectionProfile? currentConnection)
    {
        _network = network;
        _currentConnection = currentConnection;
        
        Ssid = network.Ssid;
        Bssid = network.Bssid;
        NetworkId = Ssid + "_" + Bssid;
        DisplayName = string.IsNullOrEmpty(Ssid) ? "Hidden Network" : Ssid;
        NetworkKind = network.NetworkKind;
        SignalStrength = network.SignalBars; // 0-5 bars
        UpdateSignalGlyph();
        
        UpdateConnectionState(currentConnection);
        CheckIfKnownNetwork();
    }

    public void Update(WiFiAvailableNetwork network, ConnectionProfile? currentConnection)
    {
        _network = network;
        _currentConnection = currentConnection;
        
        SignalStrength = network.SignalBars;
        UpdateSignalGlyph();
        
        UpdateConnectionState(currentConnection);
    }

    public void UpdateConnectionState(ConnectionProfile? currentConnection)
    {
        _currentConnection = currentConnection;
        
        if (currentConnection?.IsWlanConnectionProfile == true)
        {
            var connectedSsid = currentConnection.WlanConnectionProfileDetails?.GetConnectedSsid();
            IsConnected = connectedSsid == Ssid;
        }
        else
        {
            IsConnected = false;
        }
    }

    private void CheckIfKnownNetwork()
    {
        // Check if this network has a saved profile
        try
        {
            var profiles = NetworkInformation.GetConnectionProfiles();
            IsKnownNetwork = profiles.Any(p => 
                p.IsWlanConnectionProfile && 
                p.WlanConnectionProfileDetails?.GetConnectedSsid() == Ssid);
        }
        catch
        {
            IsKnownNetwork = false;
        }
    }

    private void UpdateSignalGlyph()
    {
        // Segoe Fluent Icons WiFi signal glyphs
        SignalGlyph = SignalStrength switch
        {
            0 => "\uE703", // Wifi disabled/weakest
            1 => "\uE706", // Wifi 1 bar
            2 => "\uE707", // Wifi 2 bars
            3 => "\uE708", // Wifi 3 bars
            4 => "\uE709", // Wifi 4 bars
            5 => "\uE70A", // Wifi 5 bars (full)
            _ => "\uE701", // Default wifi
        };
    }

    public async Task ToggleConnectionAsync(WiFiAdapter? adapter)
    {
        if (adapter == null || _network == null) return;

        try
        {
            if (IsConnected)
            {
                // Disconnect - launch WiFi settings to disconnect
                await Launcher.LaunchUriAsync(new Uri("ms-settings:network-wifi"));
            }
            else
            {
                // Connect - launch WiFi settings to connect
                await Launcher.LaunchUriAsync(new Uri("ms-settings:network-wifi"));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[WifiNetwork] Toggle connection failed: {ex}");
        }
    }

    public async Task ForgetAsync()
    {
        try
        {
            // Find and delete the connection profile
            var profiles = NetworkInformation.GetConnectionProfiles();
            var profile = profiles.FirstOrDefault(p => 
                p.IsWlanConnectionProfile && 
                p.WlanConnectionProfileDetails?.GetConnectedSsid() == Ssid);

            if (profile != null)
            {
                // Delete the profile
                // Note: In UWP, we can't directly delete profiles programmatically
                // User must do it via Settings. We'll launch the settings page.
                await Launcher.LaunchUriAsync(new Uri("ms-settings:network-wifimanageknownnetworks"));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[WifiNetwork] Forget failed: {ex}");
        }
    }
}