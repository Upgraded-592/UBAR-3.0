using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using UbarShared.Configuration;

namespace Ubar3;

public sealed class WeatherModule : IUbarModule
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private ModuleContext? _ctx;
    private DispatcherQueue? _uiDispatcher;
    private Border? _host;
    private TextBlock? _tempText;
    private FontIcon? _icon;
    private DispatcherTimer? _timer;
    private GeneralSettings? _settings;

    public string Id => "weather";
    public string DisplayName => "Weather";
    public string Glyph => "\uE97E";
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 2;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _ctx = ctx;
        _uiDispatcher = DispatcherQueue.GetForCurrentThread();
        _settings = SettingsStore.LoadGeneralSettings();

        var backgroundBrush = new SolidColorBrush(Color.FromArgb(255, 32, 32, 32));
        var textBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

        _host = new Border
        {
            Background = backgroundBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        _icon = new FontIcon
        {
            Glyph = "\u2600", // ☀ default sunny
            FontFamily = new FontFamily("Segoe UI Symbol"), // monochrome, consistent with the temp modules
            FontSize = 15,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_icon);

        _tempText = new TextBlock
        {
            Text = "--°",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_tempText);

        _host.Child = panel;

        _ = FetchWeatherAsync();

        var intervalMinutes = _settings?.WeatherRefreshInterval > 0 ? _settings.WeatherRefreshInterval : 15;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(intervalMinutes) };
        _timer.Tick += async (_, _) => await FetchWeatherAsync();
        _timer.Start();

        return _host;
    }

    private async Task FetchWeatherAsync()
    {
        try
        {
            if (_settings == null) _settings = SettingsStore.LoadGeneralSettings();
            
            // Check environment variable first, then fall back to settings
            string apiKey = Environment.GetEnvironmentVariable("TOMORROW_API_KEY") 
                ?? _settings.WeatherApiKey;
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                UpdateUI("--°", "\u2600", "No API key configured (set TOMORROW_API_KEY env var or in settings)");
                return;
            }

            string query;
            string? city = null;

            if (_settings.WeatherLocationMode == "manual" &&
                (_settings.WeatherLatitude != 0 || _settings.WeatherLongitude != 0))
            {
                // Invariant culture: some locales format decimals with commas
                query = FormattableString.Invariant($"{_settings.WeatherLatitude},{_settings.WeatherLongitude}");
            }
            else if (_settings.WeatherLocationMode == "manual" && !string.IsNullOrWhiteSpace(_settings.WeatherCityName))
            {
                query = Uri.EscapeDataString(_settings.WeatherCityName);
                city = _settings.WeatherCityName;
            }
            else
            {
                // Automatic: Windows location service (WiFi triangulation) when
                // allowed, otherwise IP geolocation; last resort auto:ip.
                var loc = await LocationService.GetLocationAsync();
                if (loc is { } l)
                {
                    query = FormattableString.Invariant($"{l.Lat:F4},{l.Lon:F4}");
                    city = l.City;
                }
                else
                {
                    query = "auto:ip";
                }
            }

            string units = _settings.WeatherUnits == "imperial" ? "imperial" : "metric";
            string url = $"https://api.tomorrow.io/v4/weather/realtime?location={query}&apikey={apiKey}&units={units}";

            var response = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            var data = doc.RootElement.GetProperty("data");
            var values = data.GetProperty("values");
            double temp = values.GetProperty("temperature").GetDouble();
            int weatherCode = values.GetProperty("weatherCode").GetInt32();
            string conditionText = GetConditionText(weatherCode);

            string iconGlyph = MapConditionToGlyph(weatherCode, conditionText);
            string unitSymbol = units == "imperial" ? "°F" : "°C";
            string tooltip = string.IsNullOrWhiteSpace(city) ? conditionText : $"{conditionText} — {city}";

            UpdateUI($"{temp:F0}{unitSymbol}", iconGlyph, tooltip);
        }
        catch (HttpRequestException ex)
        {
            Logger.Log($"[WeatherModule] Network error: {ex.Message}");
            UpdateUI("--°", "\u2600", "Network error");
        }
        catch (JsonException ex)
        {
            Logger.Log($"[WeatherModule] JSON parse error: {ex.Message}");
            UpdateUI("--°", "\u2600", "Parse error");
        }
        catch (Exception ex)
        {
            Logger.Log($"[WeatherModule] Unexpected error: {ex.Message}");
            UpdateUI("--°", "\u2600", "Error");
        }
    }

    private void UpdateUI(string temp, string iconGlyph, string tooltip)
    {
        if (_uiDispatcher == null) return;
        _uiDispatcher.TryEnqueue(() =>
        {
            if (_tempText != null) _tempText.Text = temp;
            if (_icon != null) _icon.Glyph = iconGlyph;
            if (_host != null) ToolTipService.SetToolTip(_host, tooltip);
        });
    }

    // Tomorrow.io v4 weatherCode → condition name.
    // Reference: https://docs.tomorrow.io/reference/weather-codes
    // (realtime returns the compact 4-digit codes, not the 5-digit day/night ones)
    private static string GetConditionText(int weatherCode)
    {
        return weatherCode switch
        {
            1000 => "Clear, Sunny",
            1100 => "Mostly Clear",
            1101 => "Partly Cloudy",
            1102 => "Mostly Cloudy",
            1001 => "Cloudy",
            2000 => "Fog",
            2100 => "Light Fog",
            3000 => "Light Wind",
            3001 => "Wind",
            3002 => "Strong Wind",
            4000 => "Drizzle",
            4001 => "Rain",
            4200 => "Light Rain",
            4201 => "Heavy Rain",
            5000 => "Snow",
            5001 => "Flurries",
            5100 => "Light Snow",
            5101 => "Heavy Snow",
            6000 => "Freezing Drizzle",
            6001 => "Freezing Rain",
            6200 => "Light Freezing Rain",
            6201 => "Heavy Freezing Rain",
            7000 => "Ice Pellets",
            7101 => "Heavy Ice Pellets",
            7102 => "Light Ice Pellets",
            8000 => "Thunderstorm",
            _ => "Unknown"
        };
    }

    // Tomorrow.io weatherCode → monochrome Segoe UI Symbol glyph.
    // Supplementary-plane escapes MUST use \U with 8 hex digits (\u is exactly 4).
    private static string MapConditionToGlyph(int code, string text)
    {
        return code switch
        {
            1000 => "\u2600",       // ☀ Clear
            1100 => "\U0001F325",   // 🌤 Mostly Clear
            1101 => "\u26C5",       // ⛅ Partly Cloudy
            1102 => "\u2601",       // ☁ Mostly Cloudy
            1001 => "\u2601",       // ☁ Cloudy
            2000 => "\U0001F32B",   // 🌫 Fog
            2100 => "\U0001F32B",   // 🌫 Light Fog
            3000 => "\U0001F32C",   // 🌬 Light Wind
            3001 => "\U0001F32C",   // 🌬 Wind
            3002 => "\U0001F32C",   // 🌬 Strong Wind
            4000 => "\U0001F327",   // 🌧 Drizzle
            4001 => "\U0001F327",   // 🌧 Rain
            4200 => "\U0001F327",   // 🌧 Light Rain
            4201 => "\U0001F327",   // 🌧 Heavy Rain
            5000 => "\u2744",       // ❄ Snow
            5001 => "\u2744",       // ❄ Flurries
            5100 => "\u2744",       // ❄ Light Snow
            5101 => "\U0001F328",   // 🌨 Heavy Snow
            6000 => "\U0001F328",   // 🌨 Freezing Drizzle
            6001 => "\U0001F328",   // 🌨 Freezing Rain
            6200 => "\U0001F328",   // 🌨 Light Freezing Rain
            6201 => "\U0001F328",   // 🌨 Heavy Freezing Rain
            7000 => "\U0001F328",   // 🌨 Ice Pellets
            7101 => "\U0001F328",   // 🌨 Heavy Ice Pellets
            7102 => "\U0001F328",   // 🌨 Light Ice Pellets
            8000 => "\u26C8",       // ⛈ Thunderstorm
            _ => "\u2600"           // ☀ Default
        };
    }

    public void OnEnabled()
    {
        _timer?.Start();
        _ = FetchWeatherAsync();
    }

    public void OnDisabled()
    {
        _timer?.Stop();
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _http.Dispose();
    }
}