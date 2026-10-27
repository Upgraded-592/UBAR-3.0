using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Ubar3;

/// <summary>
/// Bluetooth module for UBAR - manages paired Bluetooth devices with connect/disconnect/forget/rename.
/// </summary>
public sealed class BluetoothModule : IUbarModule
{
    public string Id => "bluetooth";
    public string DisplayName => "Bluetooth";
    public string Glyph => "\uE702"; // Fluent: Bluetooth
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 0;

    private readonly ObservableCollection<BluetoothDeviceViewModel> _devices = new();
    private DeviceWatcher? _watcher;
    private Radio? _bluetoothRadio;
    private DispatcherQueueTimer? _refreshTimer;
    private Button? _toggleButton;
    private TextBlock? _statusText;
    private FontIcon? _glyphIcon;
    private Flyout? _flyout;
    private bool _flyoutOpen;
    private ListView? _deviceList;
    private ToggleSwitch? _radioToggle;
    private readonly DispatcherQueue _uiDispatcher = DispatcherQueue.GetForCurrentThread();
    private bool _isUpdatingRadioToggle = false;
    private bool _isRefreshing = false;
    private bool _isDisposed = false;

    public FrameworkElement Initialize(ModuleContext context)
    {
        _glyphIcon = new FontIcon
        {
            Glyph = "\uE702",
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
        ToolTipService.SetToolTip(_toggleButton, "Click to open Bluetooth devices");

        // Build flyout (shown directly on the bar)
        BuildFlyout();
        _toggleButton.Click += (_, _) =>
        {
            if (_flyout == null || _toggleButton == null) return;
            if (_flyoutOpen)
            {
                _flyout.Hide();
                _flyoutOpen = false;
            }
            else
            {
                _flyout.ShowAt(_toggleButton);
                _flyoutOpen = true;
            }
        };

        var root = new Border
        {
            Child = _toggleButton,
            CornerRadius = new CornerRadius(16),
            Background = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumBrush"],
        };

        // Initialize Bluetooth radio and device watcher
        _ = InitializeBluetoothAsync();

        // Periodic refresh for connection state changes
        _refreshTimer = _uiDispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += async (_, _) => await RefreshDeviceStatesAsync();
        _refreshTimer.Start();

        return root;
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
        if (_isDisposed) return;
        _isDisposed = true;

        _refreshTimer?.Stop();
        _refreshTimer = null;
        
        if (_watcher != null)
        {
            _watcher.Added -= OnDeviceAdded;
            _watcher.Updated -= OnDeviceUpdated;
            _watcher.Removed -= OnDeviceRemoved;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stop();
            _watcher = null;
        }
        
        if (_bluetoothRadio != null)
        {
            _bluetoothRadio.StateChanged -= OnRadioStateChanged;
            _bluetoothRadio = null;
        }
        
        if (_radioToggle != null)
        {
            _radioToggle.Toggled -= OnRadioToggleToggled;
            _radioToggle = null;
        }

        if (_deviceList != null)
        {
            _deviceList.ContainerContentChanging -= OnDeviceListContainerContentChanging;
            _deviceList = null;
        }

        // Dispose all device view models
        foreach (var device in _devices)
        {
            device.Dispose();
        }
        _devices.Clear();
    }

    private async Task InitializeBluetoothAsync()
    {
        try
        {
            // Get Bluetooth radio
            var radios = await Radio.GetRadiosAsync();
            _bluetoothRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);

            if (_bluetoothRadio == null)
            {
                UpdateButtonUI("No BT Radio", "\uE703", Microsoft.UI.Colors.Gray);
                return;
            }

            _bluetoothRadio.StateChanged += OnRadioStateChanged;

            // Start device watcher for PAIRED Bluetooth devices (true = paired)
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            _watcher = DeviceInformation.CreateWatcher(selector);
            _watcher.Added += OnDeviceAdded;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Start();

            // Initialize radio toggle after watcher is set up
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!_isDisposed && _radioToggle != null && _bluetoothRadio != null)
                {
                    _radioToggle.IsOn = _bluetoothRadio.State == RadioState.On;
                    _radioToggle.Toggled += OnRadioToggleToggled;
                }
            });

            await UpdateRadioStateUIAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[BluetoothModule] Init failed: {ex}");
            UpdateButtonUI("Error", "\uE703", Microsoft.UI.Colors.Gray);
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        if (!_isDisposed)
        {
            _uiDispatcher.TryEnqueue(async () => await UpdateRadioStateUIAsync());
        }
    }

    private async Task UpdateRadioStateUIAsync()
    {
        if (_isDisposed) return;
        
        if (_bluetoothRadio == null)
        {
            UpdateButtonUI("No Radio", "\uE703", Microsoft.UI.Colors.Gray);
            return;
        }

        var isOn = _bluetoothRadio.State == RadioState.On;
        var connectedCount = _devices.Count(d => d.IsConnected);

        // Update radio toggle in flyout (guard against recursive updates)
        if (_radioToggle != null && !_isUpdatingRadioToggle)
        {
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!_isDisposed)
                {
                    _isUpdatingRadioToggle = true;
                    try { _radioToggle!.IsOn = isOn; }
                    finally { _isUpdatingRadioToggle = false; }
                }
            });
        }

        if (!isOn)
        {
            UpdateButtonUI("Off", "\uE703", Microsoft.UI.Colors.Gray);
        }
        else if (connectedCount > 0)
        {
            UpdateButtonUI($"{connectedCount} connected", "\uE702", GetAccentColor());
        }
        else
        {
            UpdateButtonUI("On", "\uE702", GetAccentColor());
        }
    }

    private void OnRadioToggleToggled(object sender, RoutedEventArgs e)
    {
        if (_isDisposed || _isUpdatingRadioToggle || _bluetoothRadio == null) return;
        
        var turnOn = _radioToggle!.IsOn;
        _ = ToggleRadioAsync(turnOn);
    }

    private void UpdateButtonUI(string status, string glyph, Windows.UI.Color color)
    {
        _uiDispatcher.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                if (_statusText != null) _statusText.Text = status;
                if (_glyphIcon != null) _glyphIcon.Glyph = glyph;
                if (_glyphIcon != null) _glyphIcon.Foreground = new SolidColorBrush(color);
            }
        });
    }

    private Windows.UI.Color GetAccentColor()
    {
        try
        {
            var brush = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            return brush.Color;
        }
        catch
        {
            return Windows.UI.Color.FromArgb(255, 0, 120, 212);
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

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInfo)
    {
        if (!_isDisposed)
        {
            _uiDispatcher.TryEnqueue(() => AddOrUpdateDevice(deviceInfo));
        }
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (!_isDisposed)
        {
            _uiDispatcher.TryEnqueue(() =>
            {
                var existing = _devices.FirstOrDefault(d => d.DeviceInfo.Id == update.Id);
                if (existing != null)
                {
                    existing.Update(update);
                }
            });
        }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (!_isDisposed)
        {
            _uiDispatcher.TryEnqueue(() =>
            {
                var existing = _devices.FirstOrDefault(d => d.DeviceInfo.Id == update.Id);
                if (existing != null)
                {
                    existing.Dispose();
                    _devices.Remove(existing);
                    _ = UpdateRadioStateUIAsync();
                }
            });
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        if (!_isDisposed)
        {
            _uiDispatcher.TryEnqueue(async () =>
            {
                await RefreshDeviceStatesAsync();
                await UpdateRadioStateUIAsync();
            });
        }
    }

    private void AddOrUpdateDevice(DeviceInformation deviceInfo)
    {
        if (_isDisposed) return;
        
        var existing = _devices.FirstOrDefault(d => d.DeviceInfo.Id == deviceInfo.Id);
        if (existing != null)
        {
            existing.Update(deviceInfo);
        }
        else
        {
            var vm = new BluetoothDeviceViewModel(deviceInfo);
            _devices.Add(vm);
        }
        // No need to RefreshDeviceList() - ObservableCollection + INotifyPropertyChanged handles it
    }

    private async Task RefreshDeviceStatesAsync()
    {
        if (_isDisposed || _isRefreshing) return;
        _isRefreshing = true;

        try
        {
            // Refresh connection states in parallel for better performance
            var refreshTasks = _devices.Select(d => d.RefreshConnectionStateAsync());
            await Task.WhenAll(refreshTasks);
            await UpdateRadioStateUIAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[BluetoothModule] Refresh failed: {ex}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void BuildFlyout()
    {
        _deviceList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = CreateDeviceItemTemplate(),
            ItemsSource = _devices,
            MaxHeight = 360,
        };
        _deviceList.ContainerContentChanging += OnDeviceListContainerContentChanging;

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(12, 12, 12, 8),
            Children =
            {
                new FontIcon { Glyph = "\uE702", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 },
                new TextBlock { Text = "Bluetooth Devices", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            }
        };

        _radioToggle = new ToggleSwitch
        {
            Header = "Bluetooth Radio",
            IsOn = _bluetoothRadio?.State == RadioState.On,
            Margin = new Thickness(12, 0, 12, 8),
            MinWidth = 280,
            IsEnabled = _bluetoothRadio != null,
        };
        // Event handler added in InitializeBluetoothAsync after radio is ready

        var scanButton = new Button
        {
            Content = "Scan for Devices",
            Margin = new Thickness(12, 0, 12, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        scanButton.Click += async (_, _) => await StartScanAsync();

        var content = new StackPanel
        {
            Children = { header, _radioToggle, scanButton, _deviceList }
        };

        var flyoutContent = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Padding = new Thickness(0),
            Child = content,
            Width = 320,
        };

        _flyout = new Flyout
        {
            Content = flyoutContent,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };
        _flyout.Closed += (_, _) => _flyoutOpen = false;
    }

    private DataTemplate CreateDeviceItemTemplate()
    {
        // Remove Click handlers from XAML - we'll attach them in ContainerContentChanging
        const string xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
              xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
    <Grid Padding='12,8' ColumnSpacing='12'>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width='Auto'/>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        
        <FontIcon Glyph='{Binding IconGlyph}' 
                  FontFamily='Segoe Fluent Icons' 
                  FontSize='20' 
                  Foreground='{ThemeResource AccentFillColorDefaultBrush}'
                  VerticalAlignment='Center'
                  Grid.Column='0'/>
        
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
        
        <StackPanel Grid.Column='2' Orientation='Horizontal' Spacing='4' VerticalAlignment='Center'>
            <Button x:Name='ConnectButton'
                    Content='{Binding ConnectButtonText}'
                    FontSize='11'
                    Padding='8,2'
                    MinWidth='0'
                    Height='28'
                    Visibility='{Binding ShowConnectButton}'
                    Tag='{Binding}'/>
            <Button x:Name='RenameButton'
                    Content='&#xE711;' 
                    FontFamily='Segoe Fluent Icons'
                    FontSize='12'
                    Padding='6,2'
                    MinWidth='0'
                    Height='28'
                    ToolTipService.ToolTip='Rename'
                    Tag='{Binding}'
                    Visibility='{Binding IsPaired}'/>
            <Button x:Name='ForgetButton'
                    Content='&#xE74D;' 
                    FontFamily='Segoe Fluent Icons'
                    FontSize='12'
                    Padding='6,2'
                    MinWidth='0'
                    Height='28'
                    ToolTipService.ToolTip='Forget device'
                    Tag='{Binding}'
                    Visibility='{Binding IsPaired}'
                    Foreground='{ThemeResource SystemFillColorCriticalBrush}'/>
        </StackPanel>
    </Grid>
</DataTemplate>";

        return (DataTemplate)XamlReader.Load(xaml);
    }

    private void OnDeviceListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not BluetoothDeviceViewModel vm) return;
        if (args.ItemContainer is not ListViewItem container) return;

        // Phase 0: Register for phase 1 callback where buttons are available
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(OnDeviceListContainerContentChanging);
        }
        // Phase 1: Buttons are now available in the visual tree
        else if (args.Phase == 1)
        {
            var connectBtn = FindChildByName<Button>(container, "ConnectButton");
            var renameBtn = FindChildByName<Button>(container, "RenameButton");
            var forgetBtn = FindChildByName<Button>(container, "ForgetButton");

            if (connectBtn != null)
            {
                connectBtn.Click -= OnConnectClick;
                connectBtn.Click += OnConnectClick;
            }
            if (renameBtn != null)
            {
                renameBtn.Click -= OnRenameClick;
                renameBtn.Click += OnRenameClick;
            }
            if (forgetBtn != null)
            {
                forgetBtn.Click -= OnForgetClick;
                forgetBtn.Click += OnForgetClick;
            }
        }
        // Phase 2: Clean up callback registration
        else if (args.Phase == 2)
        {
            // No further phases needed
        }
    }

    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
                return element;
            
            var result = FindChildByName<T>(child, name);
            if (result != null)
                return result;
        }
        return null;
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;
        
        if (sender is Button btn && btn.Tag is BluetoothDeviceViewModel vm)
        {
            var (success, message) = await vm.RequestConnectionAsync(!vm.IsConnected);
            if (!success && !string.IsNullOrEmpty(message))
            {
                await ShowErrorDialog(message);
            }
        }
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;
        
        if (sender is Button btn && btn.Tag is BluetoothDeviceViewModel vm)
        {
            await vm.RequestRenameAsync();
        }
    }

    private async void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (_isDisposed) return;
        
        if (sender is Button btn && btn.Tag is BluetoothDeviceViewModel vm)
        {
            var result = await ShowConfirmDialog($"Forget \"{vm.DisplayName}\"? This will unpair the device.");
            if (result)
            {
                var (success, message) = await vm.ForgetAsync();
                if (success)
                {
                    vm.Dispose();
                    _devices.Remove(vm);
                }
                else if (!string.IsNullOrEmpty(message))
                {
                    await ShowErrorDialog(message);
                }
            }
        }
    }

    private async Task ToggleRadioAsync(bool turnOn)
    {
        if (_isDisposed || _bluetoothRadio == null) return;

        try
        {
            if (turnOn)
                await _bluetoothRadio.SetStateAsync(RadioState.On);
            else
                await _bluetoothRadio.SetStateAsync(RadioState.Off);

            await UpdateRadioStateUIAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"[BluetoothModule] Toggle radio failed: {ex}");
            // Revert toggle on failure
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!_isDisposed && _radioToggle != null)
                {
                    _isUpdatingRadioToggle = true;
                    try { _radioToggle.IsOn = !turnOn; }
                    finally { _isUpdatingRadioToggle = false; }
                }
            });
        }
    }

    private async Task StartScanAsync()
    {
        if (_isDisposed || _watcher == null) return;

        try
        {
            // For paired devices, restarting the watcher refreshes the list
            // For discovering new devices, a separate watcher with pairingState=false would be needed
            _watcher.Stop();
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Logger.Log($"[BluetoothModule] Scan failed: {ex}");
        }
    }

    private async Task<bool> ShowConfirmDialog(string message)
    {
        if (_isDisposed) return false;
        
        var dialog = new ContentDialog
        {
            Title = "Confirm",
            Content = message,
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _toggleButton?.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task ShowErrorDialog(string message)
    {
        if (_isDisposed) return;
        
        var dialog = new ContentDialog
        {
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _toggleButton?.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}

/// <summary>
/// View model for a Bluetooth device in the UI.
/// Implements INotifyPropertyChanged for proper UI binding.
/// </summary>
public sealed class BluetoothDeviceViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public DeviceInformation DeviceInfo { get; }
    public BluetoothDevice? BluetoothDevice { get; private set; }
    
    // Backing fields for observable properties
    private string _displayName;
    private string _customAlias = string.Empty;
    private bool _isConnected;
    private bool _isPaired;
    private string _iconGlyph;
    private bool _isDisposed = false;

    public string DisplayName
    {
        get => !string.IsNullOrEmpty(_customAlias) ? _customAlias : _displayName;
        private set
        {
            _displayName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string IconGlyph
    {
        get => _iconGlyph;
        private set
        {
            _iconGlyph = value;
            OnPropertyChanged();
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ConnectButtonText));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public bool IsPaired
    {
        get => _isPaired;
        private set
        {
            if (_isPaired != value)
            {
                _isPaired = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ConnectButtonText));
                OnPropertyChanged(nameof(ShowConnectButton));
            }
        }
    }

    public string StatusText
    {
        get
        {
            if (IsConnected) return "Connected";
            if (IsPaired) return "Paired";
            return "Available";
        }
    }

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";

    public Visibility ShowConnectButton => IsPaired ? Visibility.Visible : Visibility.Collapsed;

    public Brush StatusColor => IsConnected
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16))
        : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public bool HasCustomAlias => !string.IsNullOrEmpty(_customAlias);

    public BluetoothDeviceViewModel(DeviceInformation deviceInfo)
    {
        DeviceInfo = deviceInfo;
        _displayName = deviceInfo.Name ?? "Unknown Device";
        _iconGlyph = GetIconGlyph(deviceInfo.Kind);
        
        // Determine actual pairing state from DeviceInformation
        _isPaired = deviceInfo.Pairing.IsPaired;
    }

    public void Update(DeviceInformationUpdate update)
    {
        // DeviceInformationUpdate doesn't provide pairing state changes directly
        // We'd need to re-query the device for that
    }

    public void Update(DeviceInformation deviceInfo)
    {
        if (_isDisposed) return;
        
        // Preserve custom alias if set
        var hadAlias = !string.IsNullOrEmpty(_customAlias);
        _displayName = deviceInfo.Name ?? _displayName;
        
        // Update pairing state
        var wasPaired = _isPaired;
        _isPaired = deviceInfo.Pairing.IsPaired;
        
        if (wasPaired != _isPaired)
        {
            OnPropertyChanged(nameof(IsPaired));
            OnPropertyChanged(nameof(ShowConnectButton));
            OnPropertyChanged(nameof(StatusText));
        }
        
        if (!hadAlias)
        {
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    private static string GetIconGlyph(DeviceInformationKind kind)
    {
        return kind switch
        {
            DeviceInformationKind.AssociationEndpoint => "\uE702",
            DeviceInformationKind.DeviceInterface => "\uE702",
            _ => "\uE702",
        };
    }

    public async Task RefreshConnectionStateAsync()
    {
        if (_isDisposed) return;
        
        try
        {
            BluetoothDevice = await BluetoothDevice.FromIdAsync(DeviceInfo.Id);
            if (BluetoothDevice != null)
            {
                IsConnected = BluetoothDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
            else
            {
                IsConnected = false;
            }
        }
        catch
        {
            IsConnected = false;
        }
    }

    /// <summary>
    /// Requests a connection or disconnection.
    /// Returns (success, errorMessage). Note: Windows does not expose a generic
    /// connect/disconnect API for all Bluetooth device types. This attempts the
    /// best-effort operation for supported device profiles.
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> RequestConnectionAsync(bool connect)
    {
        if (_isDisposed) return (false, "Device view model disposed");

        if (BluetoothDevice == null)
        {
            BluetoothDevice = await BluetoothDevice.FromIdAsync(DeviceInfo.Id);
        }

        if (BluetoothDevice == null)
        {
            return (false, "Unable to access the Bluetooth device.");
        }

        try
        {
            if (connect)
            {
                // RequestAccessAsync grants app permission to use the device,
                // it does NOT establish a Bluetooth connection.
                // Actual connection is profile-dependent (e.g., A2DP, HID, GATT).
                var access = await BluetoothDevice.RequestAccessAsync();
                if (access != DeviceAccessStatus.Allowed)
                {
                    return (false, "Access to the device was denied.");
                }
                
                // For some device types, requesting access may trigger connection.
                // Refresh state to reflect actual connection status.
                await RefreshConnectionStateAsync();
                
                // Don't claim success if not actually connected - be honest about limitations
                if (!IsConnected)
                {
                    return (false, "Access granted, but connection not established. This device type may not support programmatic connection from this app. Try connecting from Windows Settings.");
                }
                
                return (true, null);
            }
            else
            {
                // There is no universal Bluetooth disconnect API.
                // Disposing the BluetoothDevice object only releases the app's reference.
                // The OS manages the actual Bluetooth link.
                BluetoothDevice.Dispose();
                BluetoothDevice = null;
                IsConnected = false;
                return (true, "Released app reference to device. The OS manages the actual Bluetooth connection.");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[BluetoothDevice] Connection request failed: {ex}");
            return (false, $"Operation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Requests a rename. Windows does not expose a general API to rename
    /// a paired Bluetooth device from an app. This sets a local display alias
    /// that is used only within UBAR.
    /// </summary>
    public async Task RequestRenameAsync()
    {
        if (_isDisposed) return;
        
        var inputBox = new TextBox
        {
            Text = DisplayName,
            PlaceholderText = "New display name (local only)",
            MaxLength = 64,
        };

        var dialog = new ContentDialog
        {
            Title = "Rename Device (Local Only)",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "This changes the display name in UBAR only.\nThe device's actual name on other devices is unchanged.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                        FontSize = 12,
                    },
                    inputBox
                }
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = null,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(inputBox.Text))
        {
            var newName = inputBox.Text.Trim();
            if (newName != _displayName)
            {
                _customAlias = newName;
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(HasCustomAlias));
            }
        }
    }

    /// <summary>
    /// Unpairs (forgets) the device.
    /// Returns (success, errorMessage).
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> ForgetAsync()
    {
        if (_isDisposed) return (false, "Device view model disposed");

        try
        {
            // Use DeviceInformation.Pairing directly - doesn't require BluetoothDevice
            var pairing = DeviceInfo.Pairing;
            if (!pairing.IsPaired)
            {
                return (true, null); // Already unpaired
            }

            var result = await pairing.UnpairAsync();
            if (result.Status == DeviceUnpairingResultStatus.Unpaired ||
                result.Status == DeviceUnpairingResultStatus.AlreadyUnpaired)
            {
                IsPaired = false;
                return (true, null);
            }
            else
            {
                Logger.Log($"[BluetoothDevice] Unpair failed: {result.Status}");
                return (false, $"Failed to unpair device: {result.Status}");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[BluetoothDevice] Forget failed: {ex}");
            return (false, $"Failed to unpair device: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (BluetoothDevice != null)
        {
            try { BluetoothDevice.Dispose(); } catch { }
            BluetoothDevice = null;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (!_isDisposed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}