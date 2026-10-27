using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ubar3
{
    public static class LinkedUbar
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private const string BaseUrl = "https://linkedapp.ddns.net";

        public static async Task<string?> GetLinkedSpotifyTrackIdAsync(
            string title,
            string artist)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(artist))
            {
                return null;
            }

            try
            {
                string query = Uri.EscapeDataString(
                    $"{title} {artist}");

                string url =
                    $"{BaseUrl}/api/search?q={query}&type=track&limit=10&sources=spotify";

                using HttpResponseMessage response =
                    await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                string json = await response.Content.ReadAsStringAsync();

                using JsonDocument document = JsonDocument.Parse(json);

                return FindLinkedSpotifyTrackId(
                    document.RootElement,
                    title,
                    artist
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LinkedUbar] Search failed: {ex.Message}");

                return null;
            }
        }

        private static string? FindLinkedSpotifyTrackId(
            JsonElement element,
            string title,
            string artist
        )
        {
            if (!element.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // First pass: look for an exact title + artist match.
            foreach (JsonElement result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("platform", out JsonElement platform) ||
                    platform.GetString() != "spotify")
                {
                    continue;
                }

                if (!result.TryGetProperty("title", out JsonElement resultTitle) ||
                    !result.TryGetProperty("artist", out JsonElement resultArtist) ||
                    !result.TryGetProperty("platformId", out JsonElement platformId))
                {
                    continue;
                }

                string returnedTitle = resultTitle.GetString() ?? "";
                string returnedArtist = resultArtist.GetString() ?? "";
                string id = platformId.GetString() ?? "";
                Console.WriteLine($"Title is {returnedTitle}");
                Console.WriteLine($"Artist is {returnedArtist}");
                Console.WriteLine($"Id is {id}");

                if (string.Equals(returnedTitle, title,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(returnedArtist, artist,
                        StringComparison.OrdinalIgnoreCase) &&
                    IsValidSpotifyTrackId(id))
                {
                    return id;
                }
            }

            return null;
        }
    
        private static bool IsValidSpotifyTrackId(string? id)
        {
            return id is { Length: 22 } &&
                id.All(char.IsLetterOrDigit);
        }
    }
}