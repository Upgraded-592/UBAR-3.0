using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ubar3;

public sealed class SpicyUbar
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly string _apiKey;

    public SpicyUbar()
    {
        _apiKey = Environment.GetEnvironmentVariable(
            "SPICY_LYRICS_API_KEY"
        ) ?? throw new InvalidOperationException(
            "SPICY_LYRICS_API_KEY is not configured."
        );

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException(
                "SPICY_LYRICS_API_KEY is empty."
            );
        }
    }

    public async Task<LyricsResult?> GetLyricsAsync(
        string spotifyTrackId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(spotifyTrackId) ||
            spotifyTrackId.Length != 22)
        {
            throw new ArgumentException(
                "A valid 22-character Spotify track ID is required.",
                nameof(spotifyTrackId)
            );
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.spicylyrics.org/v1/lyrics/{spotifyTrackId}"
        );

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await Http.SendAsync(
            request,
            cancellationToken
        );

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken
        );

        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken
        );

        var root = document.RootElement;

        if (!root.TryGetProperty("Body", out var body) ||
            body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new LyricsResult
        {
            TrackId = GetString(body, "id") ?? spotifyTrackId,
            Source = GetString(body, "source") ?? "unknown",
            SyncType = GetString(body, "Type") ?? "Static"
        };

        if (body.TryGetProperty("Content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            int lineIndex = 0;

            foreach (var section in content.EnumerateArray())
            {
                if (!section.TryGetProperty("Lead", out var lead) ||
                    lead.ValueKind != JsonValueKind.Object)
                {
                    lineIndex++;
                    continue;
                }

                if (lead.TryGetProperty(
                        "Syllables", out var syllables) &&
                    syllables.ValueKind == JsonValueKind.Array)
                {
                    foreach (var syllable in syllables.EnumerateArray())
                    {
                        string? text = GetString(syllable, "Text");

                        if (string.IsNullOrWhiteSpace(text))
                            continue;

                        double start = GetDouble(
                            syllable, "StartTime"
                        );

                        double end = GetDouble(
                            syllable, "EndTime"
                        );

                        if (end <= start)
                            continue;

                        result.Words.Add(
                            new MusicPlayerWindow.KaraokeWord
                            {
                                Text = text,
                                StartTime = start,
                                EndTime = end,
                                LineIndex = lineIndex
                            }
                        );
                    }
                }

                lineIndex++;
            }
        }

        if (body.TryGetProperty(
                "UploadAttribution", out var attribution) &&
            attribution.ValueKind == JsonValueKind.Object)
        {
            result.UploaderName = GetNestedString(
                attribution, "Uploader", "username"
            );

            result.UploaderUrl = GetNestedString(
                attribution, "Uploader", "url"
            );

            result.MakerName = GetNestedString(
                attribution, "Maker", "username"
            );

            result.MakerUrl = GetNestedString(
                attribution, "Maker", "url"
            );
        }

        return result;
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double GetDouble(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out double number)
            ? number
            : 0;
    }

    private static string? GetNestedString(
        JsonElement element,
        string parent,
        string property)
    {
        return element.TryGetProperty(parent, out var nested) &&
               nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, property)
            : null;
    }
}

public sealed class LyricsResult
{
    public string TrackId { get; set; } = "";

    public string Source { get; set; } = "unknown";

    public string SyncType { get; set; } = "Static";

    public List<MusicPlayerWindow.KaraokeWord> Words { get; } = new();

    public string? UploaderName { get; set; }

    public string? UploaderUrl { get; set; }

    public string? MakerName { get; set; }

    public string? MakerUrl { get; set; }
}