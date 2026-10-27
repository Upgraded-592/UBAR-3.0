using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ubar3;

public sealed class ClockModule : IUbarModule
{
    private TextBlock? _text;
    private ModuleContext? _ctx;

    public string Id => "clock";
    public string DisplayName => "Clock";
    public string Glyph => "\uE917"; // Fluent: Clock
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Center;
    public int DefaultOrder => 1;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _ctx = ctx;

        Brush accent;
        try { accent = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"]; }
        catch { accent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)); }

        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(12, 6, 12, 6),
            Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemControlBackgroundChromeMediumBrush"],
            CornerRadius = new CornerRadius(16),
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(new FontIcon
        {
            Glyph = "\uE917",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _text = new TextBlock
        {
            Text = DateTime.Now.ToString("hh:mm:ss tt"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(_text);
        return host;
    }

    public void OnEnabled()  => _ctx?.Metrics.Subscribe(OnTick);
    public void OnDisabled() => _ctx?.Metrics.Unsubscribe(OnTick);
    public void Dispose() { }

    private void OnTick(SystemMetrics _)
    {
        if (_text != null) _text.Text = DateTime.Now.ToString("hh:mm:ss tt");
    }
}
