using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ubar3;

public sealed class ExitModule : IUbarModule
{
    public string Id => "exit";
    public string DisplayName => "Exit";
    public string Glyph => "\uE7E8";
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 10;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };

        // Divider
        host.Children.Add(new Border
        {
            BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"],
            BorderThickness = new Thickness(1, 0, 0, 0),
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var btn = new Button
        {
            Background = null,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Height = 24, Width = 24,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE7E8", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12 },
        };
        btn.PointerEntered += (s, e) => btn.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 77, 77));
        btn.PointerExited  += (s, e) => btn.ClearValue(Button.ForegroundProperty);
        ToolTipService.SetToolTip(btn, "Exit UBAR");
        btn.Click += (s, e) =>
        {
            Logger.Log("[ExitModule] User clicked exit.");
            Microsoft.UI.Xaml.Application.Current.Exit();
        };
        host.Children.Add(btn);
        return host;
    }

    public void OnEnabled() { }
    public void OnDisabled() { }
    public void Dispose() { }
}
