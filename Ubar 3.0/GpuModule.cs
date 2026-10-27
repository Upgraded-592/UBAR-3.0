using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Colors;

namespace Ubar3;

public sealed class GpuModule : IUbarModule
{
    private TextBlock? _text;
    private ProgressBar? _usageBar;
    private FontIcon? _icon;

    private HardwareMonitoringService? _monitor;

    public string Id => "gpu";
    public string DisplayName => "GPU Usage";
    public string Glyph => "\uEDA3"; // Fluent: GPU
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 13;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _monitor = ctx.Metrics as HardwareMonitoringService;

        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(host, "GPU Usage");

        _icon = new FontIcon
        {
            Glyph = "\uEDA3",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Opacity = 0.8
        };
        host.Children.Add(_icon);

        // Usage text and bar
        var usagePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        _text = new TextBlock { Text = "GPU: --%", FontSize = 12, Width = 55, VerticalAlignment = VerticalAlignment.Center };
        usagePanel.Children.Add(_text);
        _usageBar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 40, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        usagePanel.Children.Add(_usageBar);
        host.Children.Add(usagePanel);

        return host;
    }

    public void OnEnabled() => _monitor?.SubscribeExtended(OnTick);
    public void OnDisabled() => _monitor?.UnsubscribeExtended(OnTick);
    public void Dispose() { }

    private void OnTick(ExtendedSystemMetrics m)
    {
        if (_text == null || _usageBar == null || _icon == null) 
            return;

        bool hasUsage = m.GpuUsagePercent.HasValue;

        if (hasUsage)
        {
            double usage = m.GpuUsagePercent.GetValueOrDefault();
            _text.Text = $"GPU: {usage:F0}%";
            _usageBar.Value = usage;

            _text.Foreground = usage switch
            {
                >= 90 => new SolidColorBrush(Red),
                >= 70 => new SolidColorBrush(Orange),
                >= 50 => new SolidColorBrush(Yellow),
                _ => new SolidColorBrush(LimeGreen)
            };
            _usageBar.Foreground = _text.Foreground;
        }
        else
        {
            _text.Text = "GPU: --%";
            _usageBar.Value = 0;
            _text.Foreground = new SolidColorBrush(Gray);
            _usageBar.Foreground = new SolidColorBrush(Gray);
        }

        // Overall icon color based on GPU usage
        if (hasUsage)
        {
            double usage = m.GpuUsagePercent.GetValueOrDefault();
            _icon.Foreground = usage switch
            {
                >= 90 => new SolidColorBrush(Red),
                >= 70 => new SolidColorBrush(Orange),
                >= 50 => new SolidColorBrush(Yellow),
                _ => new SolidColorBrush(LimeGreen)
            };
        }
        else
        {
            _icon.Foreground = new SolidColorBrush(Gray);
        }
    }
}