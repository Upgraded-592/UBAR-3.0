using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ubar3;

public sealed class RamModule : IUbarModule
{
    private TextBlock? _text;
    private ProgressBar? _bar;
    private ModuleContext? _ctx;

    public string Id => "ram";
    public string DisplayName => "RAM Usage";
    public string Glyph => "\uEEA0"; // Fluent: RAM
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 1;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _ctx = ctx;
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(host, "Memory Usage");
        host.Children.Add(new FontIcon { Glyph = "\uEEA0", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 13, Opacity = 0.8 });
        _text = new TextBlock { Text = "RAM: --%", FontSize = 12, Width = 55, VerticalAlignment = VerticalAlignment.Center };
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
        if (_text != null) _text.Text = $"RAM: {m.RamUsage:F0}%";
        if (_bar  != null) _bar.Value  = m.RamUsage;
    }
}
