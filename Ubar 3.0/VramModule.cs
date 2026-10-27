using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ubar3;

public sealed class VramModule : IUbarModule
{
    private TextBlock? _text;
    private ProgressBar? _bar;
    private FontIcon? _icon;
    private HardwareMonitoringService? _monitor;

    public string Id => "vram";
    public string DisplayName => "VRAM Usage";
    public string Glyph => "\uE964"; // SmartCardVisual
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 14;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _monitor = ctx.Metrics as HardwareMonitoringService;

        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(host, "VRAM Usage");

        _icon = new FontIcon
        {
            Glyph = "\uE964", // SmartCardVisual
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Opacity = 0.8
        };
        host.Children.Add(_icon);

        _text = new TextBlock { Text = "VRAM: --%", FontSize = 12, Width = 60, VerticalAlignment = VerticalAlignment.Center };
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

        if (m.GpuMemoryUsagePercent.HasValue)
        {
            double mem = m.GpuMemoryUsagePercent.Value;
            _text.Text = $"VRAM: {mem:F0}%";
            _bar.Value = mem;

            var brush = mem switch
            {
                >= 90 => new SolidColorBrush(Microsoft.UI.Colors.Red),
                >= 70 => new SolidColorBrush(Microsoft.UI.Colors.Orange),
                _ => new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
            };
            _text.Foreground = brush;
            _bar.Foreground = brush;
            _icon.Foreground = brush;
        }
        else
        {
            _text.Text = "VRAM: --%";
            _bar.Value = 0;
            _text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            _bar.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            _icon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
    }
}