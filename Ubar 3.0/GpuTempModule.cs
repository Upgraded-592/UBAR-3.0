using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ubar3;

public sealed class GpuTempModule : IUbarModule
{
    private TextBlock? _text;
    private ProgressBar? _bar;
    private FontIcon? _icon;
    private HardwareMonitoringService? _monitor;

    public string Id => "gputemp";
    public string DisplayName => "GPU Temperature";
    public string Glyph => "\uD83C\uDF21"; // 🌡 thermometer (monochrome via Segoe UI Symbol)
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 12;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _monitor = ctx.Metrics as HardwareMonitoringService;

        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(host, "GPU Temperature");

        _icon = new FontIcon
        {
            Glyph = "\uD83C\uDF21", // 🌡 thermometer
            FontFamily = new FontFamily("Segoe UI Symbol"), // monochrome code-style icon, not the color emoji
            FontSize = 15, // emoji glyphs render smaller than Fluent PUA glyphs, so bump up to match the 13px neighbors
            Opacity = 0.8
        };
        host.Children.Add(_icon);

        _text = new TextBlock { Text = "GPU: --°C", FontSize = 12, Width = 65, VerticalAlignment = VerticalAlignment.Center };
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

        if (m.GpuTemperatureCelsius.HasValue)
        {
            double temp = m.GpuTemperatureCelsius.Value;
            _text.Text = $"GPU: {temp:F0}°C";
            _bar.Value = Math.Clamp(temp, 0, 100);

            _text.Foreground = temp switch
            {
                >= 85 => new SolidColorBrush(Microsoft.UI.Colors.Red),
                >= 75 => new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                >= 65 => new SolidColorBrush(Microsoft.UI.Colors.Orange),
                >= 55 => new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                _ => new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
            };
            _bar.Foreground = _text.Foreground;
            _icon.Foreground = _text.Foreground;
        }
        else
        {
            _text.Text = "GPU: N/A";
            _bar.Value = 0;
            _text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            _bar.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            _icon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
    }
}