using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ubar3;

public sealed class CalendarModule : IUbarModule
{
    private ModuleContext? _ctx;
    private DispatcherQueue? _uiDispatcher;
    private Border? _host;
    private TextBlock? _dateText;
    private DispatcherTimer? _timer;

    public string Id => "calendar";
    public string DisplayName => "Calendar";
    public string Glyph => "\uE787";
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Center;
    public int DefaultOrder => 0;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _ctx = ctx;
        _uiDispatcher = DispatcherQueue.GetForCurrentThread();

        var backgroundBrush = new SolidColorBrush(Color.FromArgb(255, 32, 32, 32));
        var textBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

        _host = new Border
        {
            Background = backgroundBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _dateText = new TextBlock
        {
            Text = DateTime.Now.ToString("ddd, MMM d"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = textBrush,
        };

        _host.Child = _dateText;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => UpdateDate();
        _timer.Start();

        UpdateDate();

        return _host;
    }

    private void UpdateDate()
    {
        if (_dateText != null)
        {
            _dateText.Text = DateTime.Now.ToString("ddd, MMM d");
        }
    }

    public void OnEnabled()
    {
        _timer?.Start();
    }

    public void OnDisabled()
    {
        _timer?.Stop();
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}