using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ubar3;

public sealed class CpuModule : IUbarModule
{
    private TextBlock? _text;
    private ProgressBar? _bar;
    private ModuleContext? _ctx;

    public string Id => "cpu";
    public string DisplayName => "CPU Usage";
    public string Glyph => "\uEEA1"; // Fluent: CPU
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 0;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _ctx = ctx;
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(host, "CPU Usage");
        host.Children.Add(new FontIcon { Glyph = "\uEEA1", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 13, Opacity = 0.8 });
        _text = new TextBlock { Text = "CPU: --%", FontSize = 12, Width = 55, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(_text);
        _bar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 40, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(_bar);
        return host;
    }

    public void OnEnabled()  => _ctx?.Metrics.Subscribe(OnTick);
    public void OnDisabled() => _ctx?.Metrics.Unsubscribe(OnTick);
    public void Dispose() { }

    private void OnTick(SystemMetrics m)
    {
        if (_text != null) _text.Text = $"CPU: {m.CpuUsage:F0}%";
        if (_bar  != null) _bar.Value  = m.CpuUsage;
    }
}
