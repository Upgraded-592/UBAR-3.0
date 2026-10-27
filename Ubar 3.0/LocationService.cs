using System.Text.Json;
using Windows.Devices.Geolocation;

namespace Ubar3;

/// <summary>Resolved location for the weather widget.</summary>
internal sealed record LocationInfo(double Lat, double Lon, string? City, string Source);

/// <summary>
/// Resolves the machine's current location for the weather widget.
/// Strategy: Windows location service (WiFi triangulation; consent lives in
/// Settings › Privacy › Location › "Let desktop apps access your location")
/// → IP geolocation (city-level) → null, which makes the weather provider
/// fall back to server-side IP geolocation ("auto:ip").
///
/// Re-resolves every cacheTtl (kept short so laptops that move get fresh
/// weather for their current area). The city label is reverse-geocoded only
/// when the position moves between ~5 km grid cells, so staying put costs
/// no extra geocoder traffic.
/// </summary>
internal static class LocationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private static LocationInfo? _cached;
    private static DateTimeOffset _cacheTime;
    private static bool _windowsLocationUnavailable; // sticky for the session

    private static string? _cityCacheValue;
    private static (int Lat, int Lon) _cityCacheKey = (int.MinValue, int.MinValue);

    /// <summary>
    /// Returns the current location, or null if nothing works (caller falls
    /// back to auto:ip). Never throws.
    /// </summary>
    public static async Task<LocationInfo?> GetLocationAsync()
    {
        if (_cached is { } cached && DateTimeOffset.UtcNow - _cacheTime < CacheTtl)
            return cached;

        // 1) Windows location — accurate, works on laptops that roam.
        if (!_windowsLocationUnavailable)
        {
            try
            {
                // Needs the UI thread; for unpackaged desktop apps there is no
                // per-app prompt — the "Let desktop apps access your location"
                // toggle decides.
                var access = await Geolocator.RequestAccessAsync();
                if (access == GeolocationAccessStatus.Allowed)
                {
                    var geolocator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
                    var op = geolocator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10));
                    var position = await op.AsTask().WaitAsync(TimeSpan.FromSeconds(15));

                    var pos = position.Coordinate.Point.Position;
                    if (!double.IsNaN(pos.Latitude) && !double.IsNaN(pos.Longitude) &&
                        !(pos.Latitude == 0 && pos.Longitude == 0))
                    {
                        var city = await ReverseGeocodeAsync(pos.Latitude, pos.Longitude);
                        _cached = new LocationInfo(pos.Latitude, pos.Longitude, city, "Windows location");
                        _cacheTime = DateTimeOffset.UtcNow;
                        Logger.Log($"[LocationService] Windows location: {pos.Latitude:F4},{pos.Longitude:F4} ({city ?? "?"})");
                        return _cached;
                    }

                    _windowsLocationUnavailable = true;
                    Logger.Log("[LocationService] Windows location returned no usable fix.");
                }
                else
                {
                    _windowsLocationUnavailable = true;
                    Logger.Log($"[LocationService] Windows location not allowed ({access}); using IP location.");
                }
            }
            catch (Exception ex)
            {
                _windowsLocationUnavailable = true;
                Logger.Log($"[LocationService] Windows location failed: {ex.Message}");
            }
        }

        // 2) IP geolocation — city-level, no permission needed.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync("https://ipapi.co/json/");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("latitude", out var latEl) &&
                root.TryGetProperty("longitude", out var lonEl) &&
                latEl.TryGetDouble(out var lat) && lonEl.TryGetDouble(out var lon) &&
                !(lat == 0 && lon == 0))
            {
                string? city = null;
                if (root.TryGetProperty("city", out var cityEl) && cityEl.ValueKind == JsonValueKind.String)
                    city = cityEl.GetString();

                _cached = new LocationInfo(lat, lon, city, "IP location");
                _cacheTime = DateTimeOffset.UtcNow;
                Logger.Log($"[LocationService] IP location: {lat:F4},{lon:F4} ({city ?? "?"})");
                return _cached;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[LocationService] IP location failed: {ex.Message}");
        }

        return null; // caller falls back to auto:ip
    }

    /// <summary>
    /// Reverse-geocodes coordinates to a city name. Cached per ~0.05° grid
    /// cell (≈5 km) — moving within the same city reuses the label.
    /// </summary>
    private static async Task<string?> ReverseGeocodeAsync(double lat, double lon)
    {
        var key = ((int)Math.Round(lat / 0.05), (int)Math.Round(lon / 0.05));
        if (key == _cityCacheKey && _cityCacheValue != null)
            return _cityCacheValue;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Ubar3-WeatherWidget/1.0");
            var url = "https://api.bigdatacloud.net/data/reverse-geocode-client" +
                      $"?latitude={FormattableString.Invariant($"{lat:F4}")}" +
                      $"&longitude={FormattableString.Invariant($"{lon:F4}")}&localityLanguage=en";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? city =
                TryGetString(root, "city") ??
                TryGetString(root, "locality") ??
                TryGetString(root, "principalSubdivision");

            if (!string.IsNullOrWhiteSpace(city))
            {
                _cityCacheKey = key;
                _cityCacheValue = city;
            }
            return city;
        }
        catch (Exception ex)
        {
            Logger.Log($"[LocationService] Reverse geocode failed: {ex.Message}");
            return null;
        }
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }
}
