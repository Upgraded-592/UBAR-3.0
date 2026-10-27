using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System.Power;

using static Microsoft.UI.Colors;

namespace Ubar3;

public sealed class BatteryModule : IUbarModule
{
    private TextBlock? _text;
    private ProgressBar? _bar;
    private FontIcon? _icon;
    private ModuleContext? _ctx;
    private HardwareMonitoringService? _monitor;

    public string Id => "battery";
    public string DisplayName => "Battery";
    public string Glyph => "\uE850"; // Default: Battery0 (official Segoe Fluent Icons)
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 10;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _ctx = ctx;
        _monitor = ctx.Metrics as HardwareMonitoringService;

        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(host, "Battery");

        _icon = new FontIcon
        {
            Glyph = "\uE850", // Battery0 (official Segoe Fluent Icons)
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Opacity = 0.8
        };
        host.Children.Add(_icon);

        _text = new TextBlock { Text = "BAT: --%", FontSize = 12, Width = 60, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(_text);

        _bar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 40, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(_bar);

        return host;
    }

    public void OnEnabled() => _monitor?.SubscribeExtended(OnTick);
    public void OnDisabled() => _monitor?.UnsubscribeExtended(OnTick);
    public void Dispose() { }

    private void OnTick(ExtendedSystemMetrics m)
    {
        if (_text == null || _bar == null || _icon == null) return;

        if (m.BatteryPercent.HasValue)
        {
            double pct = m.BatteryPercent.Value;
            _text.Text = $"BAT: {pct:F0}%";
            _bar.Value = pct;

            string glyph;
            bool isCharging = m.BatteryStatus == BatteryStatus.Charging;
            
            // Check Battery Saver status
            bool isBatterySaverOn = Windows.System.Power.PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            
            int level = (int)Math.Floor(pct / 10.0);
            level = Math.Clamp(level, 0, 10);

            if (isCharging)
            {
                // Official Segoe Fluent Icons charging glyphs (E85A-E862, E83E)
                glyph = level switch
                {
                    0 => "\uE85A", // BatteryCharging0
                    1 => "\uE85B", // BatteryCharging1
                    2 => "\uE85C", // BatteryCharging2
                    3 => "\uE85D", // BatteryCharging3
                    4 => "\uE85E", // BatteryCharging4
                    5 => "\uE85F", // BatteryCharging5
                    6 => "\uE860", // BatteryCharging6
                    7 => "\uE861", // BatteryCharging7
                    8 => "\uE862", // BatteryCharging8
                    9 => "\uE83E", // BatteryCharging9
                    10 => "\uE83F", // Battery10 (full) - also BatteryCharging9 in some versions
                    _ => "\uE83E"
                };
            }
            else
            {
                // Battery Saver icon takes priority
                if (isBatterySaverOn)
                {
                    // Official Segoe Fluent Icons Battery Saver glyphs (E863-E86B)
                    glyph = level switch
                    {
                        0 => "\uE863", // BatterySaver0
                        1 => "\uE864", // BatterySaver1
                        2 => "\uE865", // BatterySaver2
                        3 => "\uE866", // BatterySaver3
                        4 => "\uE867", // BatterySaver4
                        5 => "\uE868", // BatterySaver5
                        6 => "\uE869", // BatterySaver6
                        7 => "\uE86A", // BatterySaver7
                        8 => "\uE86B", // BatterySaver8
                        9 => "\uE86B",
                        10 => "\uE86B",
                        _ => "\uE863"
                    };
                }
                // Battery Warning for critical levels (using Battery0 as warning)
                else if (pct < 15)
                {
                    glyph = "\uE850"; // Battery0 - empty/critical
                }
                else
                {
                    // Official Segoe Fluent Icons battery glyphs (E850-E859, E83F)
                    glyph = level switch
                    {
                        0 => "\uE850", // Battery0
                        1 => "\uE851", // Battery1
                        2 => "\uE852", // Battery2
                        3 => "\uE853", // Battery3
                        4 => "\uE854", // Battery4
                        5 => "\uE855", // Battery5
                        6 => "\uE856", // Battery6
                        7 => "\uE857", // Battery7
                        8 => "\uE858", // Battery8
                        9 => "\uE859", // Battery9
                        10 => "\uE83F", // Battery10 (full)
                        _ => "\uE850"
                    };
                }
            }

            _icon.Glyph = glyph;

            // Color coding
            var textColor = pct switch
            {
                < 15 => OrangeRed,
                < 30 => Orange,
                _ => White
            };
            var brush = new SolidColorBrush(textColor);
            _text.Foreground = brush;
            _bar.Foreground = brush;
        }
        else
        {
            _text.Text = "BAT: N/A";
            _bar.Value = 0;
            _icon.Glyph = "\uE996"; // BatteryUnknown
            _text.Foreground = new SolidColorBrush(White);
            _bar.Foreground = new SolidColorBrush(White);
        }
    }
}