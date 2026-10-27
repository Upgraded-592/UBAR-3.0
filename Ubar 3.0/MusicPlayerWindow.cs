using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using System.Net.Http;
using Windows.Media.Control;
using Windows.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using static Microsoft.UI.Colors;

namespace Ubar3;

public sealed class MusicPlayerWindow : Window
{
    private Image? _albumArt;
    private TextBlock? _titleText;
    private TextBlock? _artistText;
    private Button? _preSong = null!;
    private Button? _nextSong = null!;
    private Button? _playSong = null!;
    private Button? _repeat = null!;
    private Button? _shuffle = null!;
    private MediaPlaybackAutoRepeatMode _repeatMode;
    private Slider? _progressBar;
    private RichTextBlock? _lyricsText;
    private TextBlock? _lyricsAttribution;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private FrameworkElement _musicWidget;
    private bool _updatingProcess;
    private bool _isDrifting;
    private DispatcherQueueTimer? _progTimer;

    // How the hell does chatgpt keep track of this shit.

    private readonly List<KaraokeWord> _karWords = new();
    private DispatcherQueueTimer? _karTimer;
    private readonly List<Run> _displayedLineRuns = new();
    private readonly List<int> _displayedLineWordIndices = new();
    private SolidColorBrush? _activeKaraokeBrush;
    private SolidColorBrush? _inactiveKaraokeBrush;
    private int _displayedLineIndex = -1;
    private int _lastDisplayedWordIndex = -2;
    private string? _lastTrackKey;
    private string? _lyricsLoadedTrackKey;
    private string? _lyricsLoadingTrackKey;
    private bool _metadataUpdateInProgress;
    private bool _isClosed;
    private string _lastLyricsProvider = "Unknown";
    private readonly LyricsProviderManager _lyricsProviderManager = new();

    public MusicPlayerWindow(
        GlobalSystemMediaTransportControlsSession? currentSession, FrameworkElement musicWidget)
    {
        _currentSession = currentSession;
        _musicWidget = musicWidget;
        
        var point = _musicWidget.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));

        var x = (int)(point.X + (_musicWidget.ActualWidth / 2) - 325);
        x = Math.Clamp(
            x, 
            0, 
            DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea.Width - 650
        );

        AppWindow.Move(new Windows.Graphics.PointInt32(
            (int)x, 
            (int)point.Y + (int)_musicWidget.ActualHeight + 10
        )
        );
        Title = "Music Player";

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.IsAlwaysOnTop = true;
        presenter?.SetBorderAndTitleBar(false, false);
        presenter?.IsResizable = false;
        presenter?.IsMaximizable = false;
        presenter?.IsMinimizable = false;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(650, 325));

        Activated += MusicPlayerWindow_Activated;
        Closed += MusicPlayerWindow_Closed;

        Content = BuildContent();
        SubscribeToSession(_currentSession);
        UpdateUI();
        StartKaraokeTimer();

        _activeKaraokeBrush = new SolidColorBrush(LimeGreen);
        _inactiveKaraokeBrush = new SolidColorBrush(White);

        // Progress updates are cheap and do not fetch media metadata.
        _progTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_progTimer != null)
        {
            _progTimer.Interval = TimeSpan.FromMilliseconds(22.22);
            _progTimer.Tick += (_, _) => UpdateProgressDisplay();
            _progTimer.Start();
        }
    }

    private void MusicPlayerWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Close();
        }

    }

    private void MusicPlayerWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _progTimer?.Stop();
        _progTimer = null;

        _karTimer?.Stop();
        _karTimer = null;

        UnsubscribeFromSession(_currentSession);
        _currentSession = null;
    }

    public void UpdateSession(GlobalSystemMediaTransportControlsSession? newSession)
    {
        if (ReferenceEquals(_currentSession, newSession))
            return;

        UnsubscribeFromSession(_currentSession);
        _currentSession = newSession;
        _lastTrackKey = null;
        _displayedLineIndex = -1;
        _lastDisplayedWordIndex = -2;
        SubscribeToSession(_currentSession);
        UpdateUI();
        UpdateKaraokeDisplay();
    }

    private void SubscribeToSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
    }

    private void UnsubscribeFromSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => _ = UpdateUIAsync());
    }

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateProgressDisplay();
            UpdateKaraokeDisplay();
        });
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateProgressDisplay();
            UpdateKaraokeDisplay();
            UpdatePlaybackButton();
        });
    }

    private void UpdateUI()
    {
        _ = UpdateUIAsync();
        UpdateProgressDisplay();
        UpdatePlaybackButton();
    }

    private void UpdateProgressDisplay() //just because its there, i added other things :)
    {
        if (_isClosed || _currentSession == null || _progressBar == null || _isDrifting)
            return;

        buttonContentSet();

        try
        {
            var timeline = _currentSession.GetTimelineProperties();
            if (timeline.EndTime <= TimeSpan.Zero)
                return;

            var position = GetPlaybackPosition();
            double value = Math.Clamp(position.TotalSeconds / timeline.EndTime.TotalSeconds * 100.0, 0, 100);

            _updatingProcess = true;
            _progressBar.Value = value;
            _updatingProcess = false;
        }
        catch
        {
            _updatingProcess = false;
        }

    }

    private void UpdatePlaybackButton()
    {
        if (_playSong == null || _currentSession == null)
            return;

        try
        {
            _playSong.Content = _currentSession.GetPlaybackInfo().PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "Ⅱ" : "▶";
        }
        catch
        {
            // The media session can disappear while Windows is changing tracks.
        }
    }

    private async Task UpdateUIAsync()
    {
        var session = _currentSession;
        if (_isClosed || session == null || _metadataUpdateInProgress)
            return;

        _metadataUpdateInProgress = true;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties == null || _isClosed || !ReferenceEquals(session, _currentSession))
                return;

            string trackKey = $"{properties.Title}\u001F{properties.Artist}";
            bool trackChanged = _lastTrackKey != trackKey;
            _lastTrackKey = trackKey;

            if (trackChanged)
            {
                _displayedLineIndex = -1;
                _lastDisplayedWordIndex = -2;
                _displayedLineRuns.Clear();
                _displayedLineWordIndices.Clear();

                _karWords.Clear();

                if (_lyricsText != null)
                {
                    _lyricsText.Blocks.Clear();

                    _lyricsText.Blocks.Add(new Paragraph
                    {
                        Inlines =
                        {
                            new Run
                            {
                                Text = "Finding lyrics...",
                                Foreground = new SolidColorBrush(White)
                            }
                        }
                    });
                }

                _lyricsLoadedTrackKey = null;
            }

            if (trackChanged &&
                !string.IsNullOrWhiteSpace(properties.Title) &&
                !string.IsNullOrWhiteSpace(properties.Artist) &&
                _lyricsLoadingTrackKey != trackKey &&
                _lyricsLoadedTrackKey != trackKey)
            {
                _lyricsLoadingTrackKey = trackKey;

                var timeline = session.GetTimelineProperties();

                _ = LoadLyricsForTrackAsync(
                    properties.Title,
                    properties.Artist,
                    properties.AlbumTitle ?? string.Empty,
                    timeline.EndTime > TimeSpan.Zero
                        ? timeline.EndTime.TotalSeconds
                        : null,
                    trackKey);
            }

            if (_titleText != null)
                _titleText.Text = string.IsNullOrWhiteSpace(properties.Title) ? "Go on, play smthng... OR ELSE >:)" : properties.Title;

            if (_artistText != null)
                _artistText.Text = string.IsNullOrWhiteSpace(properties.Artist) ? "Who dis shithead?" : properties.Artist;

            if (_albumArt != null && properties.Thumbnail != null)
            {
                using var stream = await properties.Thumbnail.OpenReadAsync();
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                if (!_isClosed && ReferenceEquals(session, _currentSession) && _albumArt != null)
                    _albumArt.Source = bitmap;
            }
            else if (_albumArt != null)
            {
                _albumArt.Source = null;
            }

            UpdatePlaybackButton();
            UpdateProgressDisplay();
            UpdateKaraokeDisplay();
        }
        catch
        {
            // Media sessions can disappear during track changes; the next event retries. You found the secret PISSFISH
        }
        finally
        {
            _metadataUpdateInProgress = false;
        }
    }
    private static bool IsValidSpotifyTrackId(string? trackId)
    {
        return trackId is { Length: 22 }
            && trackId.All(char.IsLetterOrDigit);
    }

    private async Task<string?> GetSpotifyTrackIdAsync(
        string title,
        string artist)
    {
        // 1. Try Linked first.
        string? spotifyId =
            await LinkedUbar.GetLinkedSpotifyTrackIdAsync(title, artist);

        if (IsValidSpotifyTrackId(spotifyId))
            return spotifyId;

        return null;
    }


    private async Task<List<KaraokeWord>?> FetchKaraokeWordsAsync(string title, string artist, string albumTitle)
    {
        string? spotifyId = await GetSpotifyTrackIdAsync(title, artist);
        var result = await _lyricsProviderManager.GetLyricsAsync(
            title,
            artist,
            string.Empty,
            spotifyId);
        _lastLyricsProvider = result?.Provider ?? "Sowwy, no one cared enough to do shit";
        return result?.Words;
    }

    private async Task LoadLyricsForTrackAsync(
        string title,
        string artist,
        string albumTitle,
        double? durationSeconds,
        string trackKey)
    {
        try
        {
            string? spotifyId = await GetSpotifyTrackIdAsync(title, artist);

            var candidate = await _lyricsProviderManager.GetLyricsAsync(
                title,
                artist,
                albumTitle,
                spotifyId,
                durationSeconds: durationSeconds);

            if (_isClosed || _lastTrackKey != trackKey)
                return;

            _karWords.Clear();

            if (candidate?.IsUsable == true &&
                candidate.SyncLevel != LyricsSyncLevel.Static)
            {
                _karWords.AddRange(candidate.Words);

                _lyricsLoadedTrackKey = trackKey;

                if (_lyricsAttribution != null)
                    _lyricsAttribution.Text = candidate.Attribution;
            }

            else
            {
                if (_lyricsText != null)
                {
                    _lyricsText.Blocks.Clear();

                    _lyricsText.Blocks.Add(new Paragraph
                    {
                        Inlines =
                        {
                            new Run
                            {
                                Text = candidate?.SyncLevel == LyricsSyncLevel.Static
                                    ? "Plain lyrics found, but synchronization is unavailable."
                                    : "No synchronized lyrics found.",
                                Foreground = new SolidColorBrush(White)
                            }
                        }
                    });
                }

                if (_lyricsAttribution != null)
                {
                    _lyricsAttribution.Text = candidate == null
                        ? "Sowwy, no one cared enough to do shit"
                        : candidate.SyncLevel == LyricsSyncLevel.Static
                            ? "Sowwy, no one wanted to waste time. Maybe you can try creating and sending it to Better Lyrics Unison or Spicy Lyrics."
                            : candidate.Attribution;
                }
            }


            _displayedLineIndex = -1;
            _lastDisplayedWordIndex = -2;

            UpdateKaraokeDisplay();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UBAR lyrics error: {ex}");
        }
        finally
        {
            if (_lyricsLoadingTrackKey == trackKey)
                _lyricsLoadingTrackKey = null;
        }
    }
    // GSMTC's timeline position is a snapshot. While playing, estimate the current
    // position from LastUpdatedTime instead of repeatedly reading a stale snapshot.
    private TimeSpan GetPlaybackPosition()
    {
        var session = _currentSession;
        if (session == null)
            return TimeSpan.Zero;

        try
        {
            var timeline = session.GetTimelineProperties();
            TimeSpan position = timeline.Position;

            if (session.GetPlaybackInfo().PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                timeline.LastUpdatedTime != default)
            {
                var sinceUpdate = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                if (sinceUpdate > TimeSpan.Zero && sinceUpdate < TimeSpan.FromSeconds(10))
                    position += sinceUpdate;
            }

            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime)
                position = timeline.EndTime;

            return position;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public sealed class KaraokeWord
    {
        public string Text { get; set; } = "";
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public int LineIndex { get; set; }
        public bool IsPartOfWord { get; set; }
    }

    private int FindActiveKaraokeWord(double elapsedSeconds)
    {
        // Search backwards so the newest word wins if timestamps overlap slightly.
        for (int i = _karWords.Count - 1; i >= 0; i--)
        {
            var word = _karWords[i];
            if (elapsedSeconds >= word.StartTime && elapsedSeconds < word.EndTime)
                return i;
        }

        return -1;
    }

    private int FindKaraokeLine(double elapsedSeconds, int activeWordIndex)
    {
        if (activeWordIndex >= 0)
            return _karWords[activeWordIndex].LineIndex;

        // During a gap between lines, keep the most recently started line visible.
        for (int i = _karWords.Count - 1; i >= 0; i--)
        {
            if (_karWords[i].StartTime <= elapsedSeconds)
                return _karWords[i].LineIndex;
        }

        return -1;
    }

    private void UpdateKaraokeDisplay()
    {
        if (_isClosed || _karWords.Count == 0 || _lyricsText == null)
            return;

        double elapsedSeconds = GetPlaybackPosition().TotalSeconds;
        int activeWordIndex = FindActiveKaraokeWord(elapsedSeconds);
        int activeLineIndex = FindKaraokeLine(elapsedSeconds, activeWordIndex);

        if (activeLineIndex < 0)
            return;

        if (activeLineIndex != _displayedLineIndex)
            BuildDisplayedLine(activeLineIndex);

        // Only change foregrounds when the active word changes. Do not clear and
        // rebuild RichTextBlock on every timer tick.
        if (activeWordIndex == _lastDisplayedWordIndex)
            return;

        _lastDisplayedWordIndex = activeWordIndex;
        for (int i = 0; i < _displayedLineRuns.Count; i++)
        {
            bool active = _displayedLineWordIndices[i] == activeWordIndex;
            _displayedLineRuns[i].Foreground = active
                ? _activeKaraokeBrush
                : _inactiveKaraokeBrush;
        }
    }

    private void BuildDisplayedLine(int lineIndex)
    {
        if (_lyricsText == null)
            return;

        var paragraph = new Paragraph();
        _displayedLineRuns.Clear();
        _displayedLineWordIndices.Clear();

        for (int i = 0; i < _karWords.Count; i++)
        {
            if (_karWords[i].LineIndex != lineIndex)
                continue;

            var run = new Run
            {
                Text = _karWords[i].Text + 
                        (_karWords[i].IsPartOfWord ? "" : " "),
                Foreground = _inactiveKaraokeBrush ?? new SolidColorBrush(White)
            };
            paragraph.Inlines.Add(run);
            _displayedLineRuns.Add(run);
            _displayedLineWordIndices.Add(i);
        }

        _lyricsText.Blocks.Clear();
        _lyricsText.Blocks.Add(paragraph);
        _displayedLineIndex = lineIndex;
        _lastDisplayedWordIndex = -2; // Force active colour to be applied to the new line.
    }

    private void StartKaraokeTimer()
    {
        _karTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_karTimer == null)
            return;

        // 30 FPS is smooth enough for highlighting, without hammering the UI thread.
        // But I changed it to 60 FPS just because.
        // Playback time is interpolated from the media timeline, not inferred from ticks.
        _karTimer.Interval = TimeSpan.FromMilliseconds(33);
        _karTimer.Tick += (_, _) => UpdateKaraokeDisplay();
        _karTimer.Start();
    }



    private UIElement BuildContent() //we are unity
    {
        _albumArt = new Image
        {
            Width = 180,
            Height = 180,
            Stretch = Stretch.UniformToFill,
        };

        _titleText = new TextBlock
        {
            Text = "Title",
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 200,
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 5)
        };

        _artistText = new TextBlock
        {
            Text = "Artist",
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 200,
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 10)
        };

//----------------------BUTTON CREATION----------------------\\

        _playSong = new Button
        { 
            Content = "Loading...",
            Width = 40,
            Height = 40,
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        
        _preSong = new Button
        {
            Content = "<<",
            Width = 40,
            Height = 40,
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        _nextSong = new Button{
            Content = ">>",
            Width = 40,
            Height = 40,
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        _repeat = new Button{
            Content = "Loading...",
            Width = 40,
            Height = 40,
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        _shuffle = new Button{
            Content = "Loading...",
            Width = 40,
            Height = 40,
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

//---------------------------------BUTTON ACTIONS-----------------------------\\

        _playSong.Click += async (_, _) =>
        {
            if (_currentSession == null)
                return;
            
            var playbackInfo = _currentSession.GetPlaybackInfo();
            
            await _currentSession.TryTogglePlayPauseAsync();
        };

        _preSong.Click += async (_, _) =>
        {
            if (_currentSession == null)
                return;

            await _currentSession.TrySkipPreviousAsync();
        };

        _nextSong.Click += async (_, _) =>
        {
            if (_currentSession == null)
                return;
            
            await _currentSession.TrySkipNextAsync();
        };

        _repeat.Click += async (_, _) =>
        {
            if (_currentSession == null)
                return;
            
            var playbackInfo = _currentSession.GetPlaybackInfo();
            _repeatMode = playbackInfo.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;

            if (_repeatMode == MediaPlaybackAutoRepeatMode.None){ 
                await _currentSession.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.List);
                }
            else if (_repeatMode == MediaPlaybackAutoRepeatMode.List){

                await _currentSession.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.Track);
                }
            else{
                await _currentSession.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.None);
                }
        };

        _shuffle.Click += async (_, _) =>
        {
            if (_currentSession == null)
                return;
            
            var playbackInfo = _currentSession.GetPlaybackInfo();
            await _currentSession.TryChangeShuffleActiveAsync(!(playbackInfo.IsShuffleActive ?? false));
        };

        _progressBar = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 50
        };


        _progressBar.ValueChanged += async (_, _) =>
        {
            if (_updatingProcess)
                return;

            if (_currentSession == null)
                return;

            var timeline = _currentSession.GetTimelineProperties();

            if (timeline.EndTime.TotalSeconds <= 0)
                return;

            var newPosition = TimeSpan.FromSeconds(
                _progressBar.Value / 100.0 * timeline.EndTime.TotalSeconds
            );
        };

        _progressBar.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(async (_, _) =>
            {
                _isDrifting = false;
                if (_currentSession == null)
                    return;

                var timeline = _currentSession.GetTimelineProperties();

                if (timeline.EndTime.TotalSeconds <= 0)
                    return;
            
                var newPosition = TimeSpan.FromSeconds(
                    _progressBar.Value / 100.0 * timeline.EndTime.TotalSeconds
                );

                await _currentSession.TryChangePlaybackPositionAsync((long)newPosition.Ticks);
            }),
            true
        );
        _progressBar.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) =>
            {
                _isDrifting = true;
            }),
            true
        );

        _lyricsText = new RichTextBlock
        {
            FontSize = 16,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = 350,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 8),
            TextIndent = 0,
            Blocks =
            {
                new Paragraph
                {
                    Inlines =
                    {
                        new Run
                        {
                            Text = "We haven't gotten any lyrics yet... sowwy :(",
                            Foreground = new SolidColorBrush(White)
                        }
                    }
                }
            }
        };
        _lyricsAttribution = new TextBlock
        {
            Text = "After all... why not... why not replace myself with the real credits",
            FontSize = 10, 
            Opacity = 0.7,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,            
        };
//---------------------------------LAYOUT----------------------------------\\

        var albumPanel = new Border
        {
            Width = 180,
            Height = 180,
            CornerRadius = new CornerRadius(10),
            Child = _albumArt,
            Margin = new Thickness(0, 0, 25, 25)
        };

        var lyricsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = 
            {
                _lyricsText,
                _lyricsAttribution
            }
        };

        var progPanel = new StackPanel {
            Orientation = Orientation.Vertical,
            Children = {
                _progressBar
            }
        };

        var topPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            Children = {
                albumPanel,
                lyricsPanel
            }
        };
        
        var controlPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(5, 0, 5, 0),
            Children = {
                _repeat,
                _preSong,
                _playSong,
                _nextSong,
                _shuffle,
            }
        };

        var midPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            Children = {
                _titleText,
                controlPanel,
                _artistText
            }
        };

        var mainPanel = new StackPanel {
            Orientation = Orientation.Vertical,
            Children = {
                topPanel,
                midPanel, 
                progPanel
            }
        };

        return new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel{
                Orientation = Orientation.Vertical,
                Children = 
                {
                    mainPanel
                }
            }
        };
    }
    private void buttonContentSet(){

        if (_currentSession == null)
            return;

        if (_shuffle == null)
            return;

        if (_repeat == null)
            return;
        
        if (_playSong == null)
            return;


        var playbackInfo = _currentSession.GetPlaybackInfo();
        _repeatMode = playbackInfo.AutoRepeatMode
            ?? MediaPlaybackAutoRepeatMode.None;

        if (playbackInfo.IsShuffleActive == true) {
            _shuffle.Content = "↳↰";
        }
        else {
            _shuffle.Content = "⇉";
        }


        if (_repeatMode == MediaPlaybackAutoRepeatMode.None){ 
            _repeat.Content = "⦸";
            }
        else if (_repeatMode == MediaPlaybackAutoRepeatMode.List){
            _repeat.Content = "↻";
            }
        else{
            _repeat.Content = "↻¹";
        }
        

        if (playbackInfo.PlaybackStatus == 
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            _playSong.Content = "||";
        }
        else
        {
            _playSong.Content = "▶";
        }
    }
}