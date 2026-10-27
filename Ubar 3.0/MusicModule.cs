using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Colors;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage.Streams;
using Windows.Media.Control;
using Microsoft.UI.Windowing;
using System.Collections.Generic;
using System;



namespace Ubar3;

public sealed class MusicModule : IUbarModule
{
    public string Id => "music";
    public string DisplayName => "Music";
    public string Glyph => "\uE189";  // Music icon
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Center;
    public int DefaultOrder => 0;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
//    private FrameworkElement _musicWidget;
    
    private bool _disposed;
    private TextBlock? _titleText;
    private TextBlock? _artistText;
    private Image? _albumArt;
    private LinearGradientBrush? _titleProgressBrush;
    private DispatcherQueueTimer? _musicTimeGoingThingy;
    private TimeSpan _duration;
    private DateTimeOffset _progStartTime;
    private Microsoft.UI.Dispatching.DispatcherQueue? _bugFixer;
    private DoubleAnimation? _progAnim;
    private DoubleAnimation? _animProg;
    private Storyboard? _progSB;
    private TimeSpan _lastPos;
    private MusicPlayerWindow? _musicFlyout;
    private void MusicFlyoutClosed(object sender, WindowEventArgs e){
        _musicFlyout = null;
    }
    private bool _musicFlyoutOpen;
    public FrameworkElement Initialize(ModuleContext context)
    {
        _bugFixer = DispatcherQueue.GetForCurrentThread();
        _musicTimeGoingThingy = _bugFixer?.CreateTimer();
        _musicTimeGoingThingy?.Interval = TimeSpan.FromMilliseconds(1);
        _musicTimeGoingThingy?.Tick += async (_, _) => await UpdateUIAsync();
        _titleProgressBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
        };

        _progAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
        };
        _progAnim.EnableDependentAnimation = true;
        _animProg = new DoubleAnimation
        {
            From = 0,
            To = 1,
        };
        _animProg.EnableDependentAnimation = true;

        _progSB = new Storyboard();

        _titleProgressBrush.GradientStops.Add(new GradientStop 
        {
            Color = SteelBlue,
            Offset = 0 
        });
         _titleProgressBrush.GradientStops.Add(new GradientStop 
        {
            Color = SteelBlue,
            Offset = 0.5
        });
         _titleProgressBrush.GradientStops.Add(new GradientStop 
        {
            Color = MediumSpringGreen,
            Offset = 0.5
        });
         _titleProgressBrush.GradientStops.Add(new GradientStop 
        {
            Color = MediumSpringGreen,
            Offset = 1
        });

        Storyboard.SetTarget(_progAnim, _titleProgressBrush.GradientStops[1]); 
        Storyboard.SetTarget(_animProg, _titleProgressBrush.GradientStops[2]);
        Storyboard.SetTargetProperty(_progAnim, "Offset");
        Storyboard.SetTargetProperty(_animProg, "Offset");
        _progSB.Children.Add(_progAnim);
        _progSB.Children.Add(_animProg);

        _titleText = new TextBlock
        {    
            Text = "Loading 1D10T Error: User too dumb to use this module.",
            Foreground = _titleProgressBrush,
        };
        _artistText = new TextBlock
        {
            Text = "WHO DIS SONG BY? EH?",
            Foreground = _titleProgressBrush,
        };
        
        _albumArt = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.UniformToFill,
        };
    
        
        var albumContainer = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(5),
            Child = _albumArt,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var textPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
        };

        Console.WriteLine("[Music Debug] Adding title to textpanel");
        textPanel.Children.Add(_titleText);
        Console.WriteLine("Same thing but artist");
        textPanel.Children.Add(_artistText);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Logger.Log("[MusicDebug] Adding albumContainer to panel");
        panel.Children.Add(albumContainer);

        Logger.Log("[MusicDebug] Adding textPanel to panel");
        panel.Children.Add(textPanel);
        
        panel.Tapped += (_, _) =>
        {
            if (_musicFlyout == null)
            {
                _musicFlyout = new MusicPlayerWindow(_currentSession, panel);
                _musicFlyout.Closed += MusicFlyoutClosed;
            }
            _musicFlyout.Activate();
        };

        _musicTimeGoingThingy?.Start();
        return panel;
    }
    public void OnEnabled()
    {
        if (_disposed)
            return;

        _ = InitializeMediaSessionAsync();
    }
    public void OnDisabled()
    {
        if (_disposed)
            return;

        UnsubscribeFromSession(_currentSession);
    }
    private void UnsubscribeFromSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null)
            return;
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        _bugFixer?.TryEnqueue(() =>
        {
            var newSession = sender.GetCurrentSession();

            if (ReferenceEquals(newSession, _currentSession))
                return;

            UnsubscribeFromSession(_currentSession);

            _currentSession = newSession;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _ = UpdateUIAsync();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        UnsubscribeFromSession(_currentSession);
        _disposed = true;
    }
	private async Task InitializeMediaSessionAsync()
    {
        _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (_sessionManager == null)
        {
            _titleText?.Text = "Media manager is sick rn. ig GET OUT OF HERE. NOW!";
            return;
        }
        _sessionManager.SessionsChanged -= OnSessionsChanged;
        _sessionManager.SessionsChanged += OnSessionsChanged;

        _currentSession = _sessionManager.GetCurrentSession();
        if (_currentSession == null)
        {
            _titleText?.Text = "hewo... anyone there 👉👈🥺";
            return;
        }
        _currentSession?.MediaPropertiesChanged += OnMediaPropertiesChanged; //PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH, PLEASE DONT CRASH
        await UpdateUIAsync();
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        await Task.Delay(100); // Wait for the media properties to be updated
        var timeline = _currentSession?.GetTimelineProperties();
        if (timeline != null) // Check if the position has changed significantly
        {
            _lastPos = timeline.Position;
            _duration = timeline.EndTime;
            _progStartTime = DateTimeOffset.UtcNow - timeline.Position;
            var progress = _duration.TotalSeconds > 0
            ? Math.Clamp(
                timeline.Position.TotalSeconds / _duration.TotalSeconds,
                0, 
                1
            )
            : 0;
             _bugFixer?.TryEnqueue(() =>
            {
                _titleProgressBrush!.GradientStops[1].Offset = progress;
                _titleProgressBrush.GradientStops[2].Offset = progress;
                _progAnim = new DoubleAnimation
                {
                    From = progress,
                    To = 1, 
                    Duration = new Duration(_duration - timeline.Position),
                    EnableDependentAnimation = true
                };

                _animProg = new DoubleAnimation
                {
                    From = progress, 
                    To = 1,
                    Duration = new Duration(_duration - timeline.Position),
                    EnableDependentAnimation = true  
                };

                Storyboard.SetTarget(_progAnim, _titleProgressBrush!.GradientStops[1]);
                Storyboard.SetTarget(_animProg, _titleProgressBrush.GradientStops[2]);

                Storyboard.SetTargetProperty(_progAnim, "Offset");
                Storyboard.SetTargetProperty(_animProg, "Offset");

                _progSB = new Storyboard();

                _progSB.Children.Add(_progAnim);
                _progSB.Children.Add(_animProg);

                _progSB.Begin();
            });
        }

        if (timeline != null)
        {
            _duration = timeline.EndTime;
            _progStartTime = DateTimeOffset.UtcNow - timeline.Position;       
        
        await UpdateUIAsync();
        }
    }
        
    private async Task UpdateUIAsync()
    {
        //If you see this, GOBBLEDYGOOK.
        var timeline = _currentSession?.GetTimelineProperties();
        var currentPosition = timeline?.Position;
        if (timeline != null)
        {
            _duration = timeline.EndTime;
            //_lastPos = currentPosition;
            _progStartTime = DateTimeOffset.UtcNow - timeline.Position;
        } 
        var posChanged = currentPosition != _lastPos;
        _lastPos = currentPosition ?? TimeSpan.Zero;
        var elapsed = DateTimeOffset.UtcNow - _progStartTime;
        var progress = _duration.TotalSeconds > 0
            ? Math.Clamp(
                elapsed.TotalSeconds / _duration.TotalSeconds, 
                0.0, 
                1.0
            ) 
            : (double?)null;

        if (posChanged && timeline != null)
        {
            if (progress.HasValue)
            {
                _bugFixer?.TryEnqueue(() =>
                {
                    _titleProgressBrush!.GradientStops[1].Offset = progress.Value;
                    _titleProgressBrush.GradientStops[2].Offset = progress.Value;
                    _progSB?.Stop();
                    _progAnim = new DoubleAnimation
                    {
                        From = progress.Value,
                        To = 1,
                        Duration = new Duration(_duration - timeline.Position),
                        EnableDependentAnimation = true,
                    };
                });
            }
        }

        if (_currentSession == null)
            return;

        var properties = await _currentSession.TryGetMediaPropertiesAsync();
        if (properties == null)
            return;
        if (_titleText != null)
            _bugFixer?.TryEnqueue(() =>
            {
                _titleText.Text = properties.Title ?? "New song? hopefully edm :)";
            });
            _bugFixer?.TryEnqueue(() =>
            {
                _artistText?.Text = properties.Artist ?? "WHO DIS SONG BY? EH?";
            });
        if (properties.Thumbnail != null)
        {
            var stream = await properties.Thumbnail.OpenReadAsync();
            _bugFixer?.TryEnqueue(async () =>
            {
                var bitmapImage = new BitmapImage();
                await bitmapImage.SetSourceAsync(stream);
                if (_albumArt != null)
                {
                    _albumArt.Source = bitmapImage;
                }
            });
        }  
    }
}