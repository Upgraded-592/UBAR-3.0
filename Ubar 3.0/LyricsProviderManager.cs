#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Ubar3;

/// <summary>
/// Queries multiple lyric sources and normalizes their results into
/// MusicPlayerWindow.KaraokeWord objects.
///
/// Provider routes:
///  1. Spicy Lyrics
///  2. AMLL search
///  3. Better Lyrics TTML
///  4. Better Lyrics KuGou
///  5. Better Lyrics QQ Music
///  6. KuGou
///  7. KuWo
///  8. lrc.cx
///  9. LRCLIB
///  10. AMLL LRCLIB-compatible API
///  11. lrc.red, using ISRC
///  12. NetEase Cloud Music
///  13. Lyrics.ovh
///
/// Important:
/// - Spicy Lyrics uses one endpoint. Its response determines whether the
///   returned source is community lyrics, Apple Music, or Spotify.
/// - All times in KaraokeWord are seconds.
/// - Line-synced lyrics are represented as one KaraokeWord per line.
/// - Word/syllable-synced lyrics retain their finer timing.
/// - A failed provider is allowed to fail without preventing other providers
///   from being queried.
/// </summary>
public sealed class LyricsProviderManager
{
    private static readonly HttpClient Http = CreateHttpClient();



    private readonly string? _spicyApiKey;

    public LyricsProviderManager()
    {
        _spicyApiKey =
            Environment.GetEnvironmentVariable("SPICY_LYRICS_API_KEY");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "UBAR-Lyrics/1.0"
        );

        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/json"
        );

        return client;
    }

    private static readonly Regex NoWordsNote = new(
        @"纯音乐|純音樂|请欣赏|請欣賞|此歌曲为没有填词|沒有填詞|无歌词|暫無歌詞|暂无歌词|此歌曲为没有填词的纯音乐，请您欣赏|" +
        @"^\W*instrumental\W*$|^\W*no lyrics\W*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex ChineseCredit = new(
        @"^\s*[一-鿿]{0,6}?(?:作词|作曲|编曲|制作|出品|监制|录音|混音|母带|吉他|贝斯|鼓|键盘|和声|弦乐|人声|策划|统筹|发行|工程师|演奏|词|曲)[一-鿿]{0,2}?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex EnglishCredit = new(
        @"^\s*(?:lyrics?|lyricist|compos(?:ed|er|ition)|writ(?:ten|er)|music|arrang(?:ed|er|ement)|produc(?:ed|er|tion)|mix(?:ed|ing)?|talkbox|rap|master(?:ed|ing)?|record(?:ed|ing)?|vocals?|performed|engineer(?:ed)?|backing vocals?|guitars?|bass|drums|keyboards?|strings?|piano)\s*(?:by)?\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex QqCredit = new(
        @"^.{0,40}\bby\s*[:：]|^.{0,30}\b(?:title|writer|publisher|lyrics?|composer|arranger|producer|vocals?|programming|engineer|mix|master)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex QrcEndMarker = new(
        @"^~+\s*end\s*~+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Finds lyrics using the available providers and returns the strongest
    /// candidate found.
    ///
    /// spotifyTrackId is optional, but required by Spicy Lyrics.
    /// isrc is optional, but helps LRCLIB and lrc.red find the exact recording.
    /// durationSeconds is optional and helps rank matching results.
    /// </summary>
    public async Task<LyricsCandidate?> GetLyricsAsync(
        string title,
        string artist,
        string album = "",
        string? spotifyTrackId = null,
        string? isrc = null,
        double? durationSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        album = album?.Trim() ?? string.Empty;

        title = CleanQuery(title);
        artist = CleanQuery(artist);

        var jobs = new List<Task<LyricsCandidate?>>
        {
            TrySpicyAsync(
                title, artist, spotifyTrackId, cancellationToken),

            TryAmllSearchAsync(
                title, artist, cancellationToken),

            TryBetterLyricsTtmlAsync(
                title, artist, album, durationSeconds, cancellationToken),

            TryBetterLyricsKugouAsync(
                title, artist, cancellationToken),

            TryBetterLyricsQqAsync(
                title, artist, cancellationToken),

            TryKuGouAsync(
                title, artist, album, durationSeconds, cancellationToken),

            Kuwo.FetchAsync(
                title, artist, cancellationToken),
            
            QQ.FetchAsync(
                title, artist, cancellationToken),

            TryLrcCxAsync(
                title, artist, cancellationToken),

            TryLrcLibAsync(
                title, artist, isrc, durationSeconds, cancellationToken),

            TryAmllLrcLibAsync(
                title, artist, isrc, durationSeconds, cancellationToken),

            TryLrcRedAsync(
                title, artist, isrc, cancellationToken),

            TryNetEaseAsync(
                title, artist, cancellationToken),

            TryLyricsOvhAsync(
                title, artist, cancellationToken),

        };

        LyricsCandidate? best = null;

        try
        {
            LyricsCandidate?[] results = await Task.WhenAll(
            jobs.Select(async job =>
            {
                try
                {
                    return await job;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Lyrics] Provider task failed: {ex.Message}");

                    return null;
                }
            }));
            foreach (LyricsCandidate? candidate in results)
            {
                if (candidate is null || candidate.Words.Count == 0)
                    continue;

                if (IsKnownPlaceholder(candidate))
                {
                    Console.WriteLine(
                        $"[Lyrics] Rejected placeholder from {candidate.Provider}");
                    continue;
                }

                candidate.Score = ScoreCandidate(
                    candidate,
                    title,
                    artist,
                    durationSeconds
                );

                Console.WriteLine(
                    $"[Lyrics] Candidate: {candidate.Provider} | " +
                    $"Priority: {candidate.ProviderPriority} | " +
                    $"Score: {candidate.Score} | " +
                    $"Sync: {candidate.SyncLevel} | " +
                    $"Words: {candidate.Words.Count}");

                if (best is null || candidate.Score > best.Score)
                    best = candidate;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[Lyrics] Provider aggregation error: {ex.Message}"
            );
        }
        Console.WriteLine($"[Lyrics] WINNER: {best?.Provider}");
        Console.WriteLine($"[Lyrics] Sync level: {best?.SyncLevel}");
        return best;
    }

    // ================================================================
    // 1. SPICY LYRICS
    // ================================================================

    private async Task<LyricsCandidate?> TrySpicyAsync(
        string title,
        string artist,
        string? spotifyTrackId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_spicyApiKey) ||
            string.IsNullOrWhiteSpace(spotifyTrackId) ||
            spotifyTrackId.Length != 22)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.spicylyrics.org/v1/lyrics/" +
                Uri.EscapeDataString(spotifyTrackId)
            );

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    _spicyApiKey
                );

            using HttpResponseMessage response = await Http.SendAsync(
                request,
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
                return null;

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(
                    cancellationToken
                )
            );

            JsonElement root = document.RootElement;

            if (!TryGetPropertyIgnoreCase(root, "Body", out var body) ||
                body.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string source = GetString(body, "source") ?? "unknown";
            string syncType = GetString(body, "Type") ?? "Unknown";

            var candidate = new LyricsCandidate
            {
                Provider = "Spicy Lyrics",
                Source = source,
                SyncType = syncType,
                TrackId = GetString(body, "id") ?? spotifyTrackId,
                ProviderPriority = 120,
                IsWordSynced = false
            };

            if (TryGetPropertyIgnoreCase(
                    body, "Content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                int lineIndex = 0;

                foreach (JsonElement segment in content.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (syncType.Equals(
                            "Line",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string? lineText = GetString(segment, "Text");
                        double start = GetDouble(segment, "StartTime");
                        double end = GetDouble(segment, "EndTime");

                        if (!string.IsNullOrWhiteSpace(lineText) &&
                            end > start)
                        {
                            candidate.Words.Add(
                                new MusicPlayerWindow.KaraokeWord
                                {
                                    Text = lineText,
                                    StartTime = start,
                                    EndTime = end,
                                    LineIndex = lineIndex
                                }
                            );
                        }

                        lineIndex++;
                        continue;
                    }

                    if (!TryGetPropertyIgnoreCase(
                            segment, "Lead", out var lead) ||
                        lead.ValueKind != JsonValueKind.Object)
                    {
                        lineIndex++;
                        continue;
                    }

                    if (TryGetPropertyIgnoreCase(
                            lead, "Syllables", out var syllables) &&
                        syllables.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement syllable
                                 in syllables.EnumerateArray())
                        {
                            string? text = GetString(syllable, "Text");
                            double start = GetDouble(syllable, "StartTime");
                            double end = GetDouble(syllable, "EndTime");

                            if (string.IsNullOrEmpty(text) || end <= start)
                                continue;

                            candidate.Words.Add(
                                new MusicPlayerWindow.KaraokeWord
                                {
                                    Text = text,
                                    StartTime = start,
                                    EndTime = end,
                                    LineIndex = lineIndex
                                }
                            );
                        }

                        candidate.IsWordSynced = true;
                    }

                    lineIndex++;
                }
            }

            // Spicy Lyrics can include community-upload attribution.
            // Commercial sources do not necessarily have these fields.
            if (TryGetPropertyIgnoreCase(
                    body, "UploadAttribution", out var attribution) &&
                attribution.ValueKind == JsonValueKind.Object)
            {
                candidate.UploaderName = GetNestedString(
                    attribution, "Uploader", "username");

                candidate.UploaderUrl = GetNestedString(
                    attribution, "Uploader", "url");

                candidate.MakerName = GetNestedString(
                    attribution, "Maker", "username");

                candidate.MakerUrl = GetNestedString(
                    attribution, "Maker", "url");
            }

            if (candidate.Words.Count == 0)
                return null;

            candidate.Attribution = BuildSpicyAttribution(candidate);

            return candidate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("Spicy Lyrics", ex);
            return null;
        }
    }

    private static string BuildSpicyAttribution(
        LyricsCandidate candidate)
    {
        string sourceName = FormatSpicySource(candidate.Source);

        if (candidate.Source.Equals(
                "spicy_lyrics",
                StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>
            {
                "Spicy Lyrics"
            };

            if (!string.IsNullOrWhiteSpace(candidate.UploaderName))
            {
                parts.Add(
                    $"Uploader: {candidate.UploaderName}"
                );
            }

            if (!string.IsNullOrWhiteSpace(candidate.MakerName))
            {
                parts.Add(
                    $"Maker: {candidate.MakerName}"
                );
            }

            return string.Join(" • ", parts);
        }

        return $"Spicy Lyrics · {sourceName}";
    }

    private static string FormatSpicySource(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "apple_music" => "Apple Music",
            "spotify" => "Spotify",
            "spicy_lyrics" => "Spicy Lyrics community",
            _ => string.IsNullOrWhiteSpace(source)
                ? "Unknown source"
                : source
        };
    }

    // ================================================================
    // 2. AMLL SEARCH
    // ================================================================

    private async Task<LyricsCandidate?> TryAmllSearchAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        string url =
            "https://api.amll.dev/v1/lyrics/search" +
            $"?title={Escape(title)}&artist={Escape(artist)}";

        try
        {
            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            if (document is null)
                return null;

            JsonElement root = document.RootElement;

            // API versions can wrap results differently, so look for common
            // response containers instead of assuming a single wrapper.
            JsonElement results = root;

            if (TryGetPropertyIgnoreCase(root, "data", out var data))
                results = data;
            else if (TryGetPropertyIgnoreCase(root, "results", out var found))
                results = found;

            if (results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in results.EnumerateArray())
                {
                    string text =
                        GetString(item, "lyrics") ??
                        GetString(item, "lrc") ??
                        GetString(item, "content") ??
                        "";

                    LyricsCandidate? candidate = ParseLrcCandidate(
                        text,
                        "AMLL Search",
                        "AMLL",
                        75
                    );

                    if (candidate is not null)
                        return candidate;
                }
            }

            if (results.ValueKind == JsonValueKind.Object)
            {
                string text =
                    GetString(results, "lyrics") ??
                    GetString(results, "lrc") ??
                    GetString(results, "content") ??
                    "";

                return ParseLrcCandidate(
                    text,
                    "AMLL Search",
                    "AMLL",
                    75
                );
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("AMLL Search", ex);
            return null;
        }
    }

    // ================================================================
    // 3. BETTER LYRICS TTML
    // ================================================================

    private async Task<LyricsCandidate?> TryBetterLyricsTtmlAsync(
        string title,
        string artist,
        string album,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        string duration = durationSeconds is > 0
            ? durationSeconds.Value.ToString(CultureInfo.InvariantCulture)
            : "";

        string url =
            "https://api.betterlyrics.org/ttml/getLyrics" +
            $"?s={Escape(title)}&a={Escape(artist)}&al={Escape(album)}&d={Escape(duration)}";

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                url,
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
                return null;

            string text = await response.Content.ReadAsStringAsync(
                cancellationToken
            );

            if (string.IsNullOrWhiteSpace(text))
                return null;

            List<MusicPlayerWindow.KaraokeWord> words =
                ParseTtml(text);

            if (words.Count == 0)
            {
                LyricsCandidate? lrc = ParseLrcCandidate(
                    text,
                    "Better Lyrics TTML",
                    "Better Lyrics",
                    100
                );

                return lrc;
            }

            return new LyricsCandidate
            {
                Provider = "Better Lyrics TTML",
                Source = "Better Lyrics",
                SyncType = "Syllable",
                Words = words,
                IsWordSynced = true,
                ProviderPriority = 100,
                Attribution = "Better Lyrics"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("Better Lyrics TTML", ex);
            return null;
        }
    }

    // ================================================================
    // 4. BETTER LYRICS KUGOU
    // ================================================================

    private async Task<LyricsCandidate?> TryBetterLyricsKugouAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        string url =
            "https://api.betterlyrics.org/kugou/getLyrics" +
            $"?s={Escape(title)}&a={Escape(artist)}";

        try
        {
            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            if (document is null)
                return null;

            string lyrics = ExtractLyricsText(document.RootElement);

            return ParseLrcCandidate(
                lyrics,
                "Better Lyrics KuGou",
                "KuGou",
                85
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("Better Lyrics KuGou", ex);
            return null;
        }
    }

    // ================================================================
    // 5. BETTER LYRICS QQ MUSIC
    // ================================================================

    private async Task<LyricsCandidate?> TryBetterLyricsQqAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        string url =
            "https://api.betterlyrics.org/qq/getLyrics" +
            $"?s={Escape(title)}&a={Escape(artist)}";

        try
        {
            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            if (document is null)
                return null;

            string lyrics = ExtractLyricsText(document.RootElement);

            // QQ QRC can contain timestamps such as (start,duration)text.
            LyricsCandidate? qrc = ParseQrcCandidate(
                lyrics,
                "Better Lyrics QQ Music",
                "QQ Music",
                90
            );

            if (qrc is not null)
                return qrc;

            return ParseLrcCandidate(
                lyrics,
                "Better Lyrics QQ Music",
                "QQ Music",
                80
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("Better Lyrics QQ Music", ex);
            return null;
        }
    }

    // ================================================================
    // 6. KuGou
    // ================================================================

    private async Task<LyricsCandidate?> TryKuGouAsync(
        string title,
        string artist,
        string album,
        double? requestedDuration,
        CancellationToken cancellationToken
    )
    {
        string keyword = $"{artist} {title}";

        try {
            string searchUrl = "https://mobileservice.kugou.com/api/v3/search/song" +
                        $"?format=json&keyword={Escape(keyword)}" +
                        "&page=1&pagesize=8";
                using JsonDocument? search = await GetJsonAsync(
            searchUrl,
            cancellationToken);

            if (search is null ||
                !TryGetPropertyIgnoreCase(
                    search.RootElement, "data", out var data) ||
                !TryGetPropertyIgnoreCase(
                    data, "info", out var songs) ||
                songs.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement song in songs.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string songTitle = GetString(song, "songname") ?? "";
                string songArtist = GetString(song, "singername") ?? "";
                string? hash = GetString(song, "hash");

                if (string.IsNullOrWhiteSpace(hash))
                    continue;

                // Reject obviously unrelated search results.
                if (!SameTrackText(songTitle, title) ||
                    !SameTrackText(songArtist, artist))
                {
                    continue;
                }

                int duration = 0;

                if (TryGetPropertyIgnoreCase(
                        song, "duration", out var durationElement))
                {
                    if (durationElement.ValueKind == JsonValueKind.Number)
                        durationElement.TryGetInt32(out duration);
                    else
                        int.TryParse(durationElement.ToString(), out duration);
                }

                if (duration <= 0 && requestedDuration is > 0)
                    duration = (int)Math.Round(requestedDuration.Value);

                // Avoid lyrics for a different-length recording.
                if (requestedDuration is > 0 &&
                    duration > 0 &&
                    Math.Abs(duration - requestedDuration.Value) > 15)
                {
                    continue;
                }

                // 2. Find lyric candidates for this song hash.
                string candidateUrl =
                    "https://krcs.kugou.com/search" +
                    "?ver=1&man=yes&client=mobi" +
                    $"&keyword={Escape(keyword)}" +
                    $"&duration={duration * 1000}" +
                    $"&hash={Escape(hash)}";

                using JsonDocument? candidateDocument =
                    await GetJsonAsync(candidateUrl, cancellationToken);

                if (candidateDocument is null ||
                    !TryGetPropertyIgnoreCase(
                        candidateDocument.RootElement,
                        "candidates",
                        out var candidates) ||
                    candidates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement candidate in candidates.EnumerateArray())
                {
                    string? id = GetString(candidate, "id");
                    string? accessKey = GetString(candidate, "accesskey");

                    if (string.IsNullOrWhiteSpace(id) ||
                        string.IsNullOrWhiteSpace(accessKey))
                    {
                        continue;
                    }

                    // 3. Prefer KRC, which contains word-level timestamps.
                    string krcUrl =
                        "https://lyrics.kugou.com/download" +
                        "?ver=1&client=pc" +
                        $"&id={Escape(id)}" +
                        $"&accesskey={Escape(accessKey)}" +
                        "&fmt=krc&charset=utf8";

                    using JsonDocument? krcDocument =
                        await GetJsonAsync(krcUrl, cancellationToken);

                    string encodedKrc = krcDocument is not null
                        ? GetString(krcDocument.RootElement, "content") ?? ""
                        : "";

                    if (!string.IsNullOrWhiteSpace(encodedKrc))
                    {
                        string? decodedKrc = DecodeKrc(encodedKrc);

                        if (!string.IsNullOrWhiteSpace(decodedKrc))
                        {
                            List<MusicPlayerWindow.KaraokeWord> words =
                                ParseKrcWords(decodedKrc);

                            if (words.Count > 0)
                            {
                                return new LyricsCandidate
                                {
                                    Provider = "Kugou",
                                    Source = "Kugou",
                                    SyncType = "Word",
                                    Words = words,
                                    IsWordSynced = true,
                                    IsUnsynchronized = false,
                                    ProviderPriority = 95,
                                    Attribution = "Kugou"
                                };
                            }
                        }
                    }

                    // 4. KRC unavailable or invalid? Try ordinary LRC.
                    string lrcUrl =
                        "https://lyrics.kugou.com/download" +
                        "?ver=1&client=pc" +
                        $"&id={Escape(id)}" +
                        $"&accesskey={Escape(accessKey)}" +
                        "&fmt=lrc&charset=utf8";

                    using JsonDocument? lrcDocument =
                        await GetJsonAsync(lrcUrl, cancellationToken);

                    string encodedLrc = lrcDocument is not null
                        ? GetString(lrcDocument.RootElement, "content") ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(encodedLrc))
                        continue;

                    try
                    {
                        string lrc = Encoding.UTF8.GetString(
                            Convert.FromBase64String(encodedLrc));

                        LyricsCandidate? fallback = ParseLrcCandidate(
                            lrc,
                            "Kugou",
                            "Kugou",
                            85);

                        if (fallback is not null)
                            return fallback;
                    }
                    catch (FormatException)
                    {
                        // Malformed Base64. Continue searching candidates.
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("Kugou", ex);
            return null;
        }
    }

    private static bool SameTrackText(string actual, string expected)
    {
        static string Normalize(string value)
        {
            value = value.ToLowerInvariant();
            return Regex.Replace(value, @"[^\p{L}\p{N}]", "");
        }

        string a = Normalize(actual);
        string b = Normalize(expected);

        return a.Length > 0 &&
            b.Length > 0 &&
            (a == b || a.Contains(b) || b.Contains(a));
    }

    // ================================================================
    // 7. KUWO
    // ================================================================



    // ================================================================
    // 8. LRC.CX
    // ================================================================

    private async Task<LyricsCandidate?> TryLrcCxAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        string url =
            "https://api.lrc.cx/api/v1/lyrics/advance" +
            $"?title={Escape(title)}&artist={Escape(artist)}";

        try
        {
            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            if (document is null)
                return null;

            string lyrics = ExtractLyricsText(document.RootElement);

            return ParseLrcCandidate(
                lyrics,
                "lrc.cx",
                "lrc.cx",
                75
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("lrc.cx", ex);
            return null;
        }
    }

    // ================================================================
    // 9. LRCLIB
    // ================================================================

    private async Task<LyricsCandidate?> TryLrcLibAsync(
        string title,
        string artist,
        string? isrc,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            // Exact search first.
            string url =
                "https://lrclib.net/api/get" +
                $"?track_name={Escape(title)}" +
                $"&artist_name={Escape(artist)}";

            if (durationSeconds is > 0)
            {
                url +=
                    $"&duration={Math.Round(durationSeconds.Value)}";
            }

            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            LyricsCandidate? candidate = document is null
                ? null
                : CandidateFromLrcLib(
                    document.RootElement,
                    "LRCLIB",
                    80
                );

            if (candidate is not null)
                return candidate;

            // Fallback to search endpoint.
            string searchUrl =
                "https://lrclib.net/api/search" +
                $"?track_name={Escape(title)}" +
                $"&artist_name={Escape(artist)}";

            using JsonDocument? search = await GetJsonAsync(
                searchUrl,
                cancellationToken
            );

            if (search is null ||
                search.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement item in search.RootElement.EnumerateArray())
            {
                candidate = CandidateFromLrcLib(
                    item,
                    "LRCLIB",
                    80
                );

                if (candidate is not null)
                    return candidate;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("LRCLIB", ex);
            return null;
        }
    }

    // ================================================================
    // 10. AMLL LRCLIB-COMPATIBLE API
    // ================================================================

    private async Task<LyricsCandidate?> TryAmllLrcLibAsync(
        string title,
        string artist,
        string? isrc,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        string url =
            "https://api.amll.dev/v1/lrclib/search" +
            $"?track_name={Escape(title)}" +
            $"&artist_name={Escape(artist)}";

        if (!string.IsNullOrWhiteSpace(isrc))
            url += $"&isrc={Escape(isrc)}";

        if (durationSeconds is > 0)
            url += $"&duration={Math.Round(durationSeconds.Value)}";

        try
        {
            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            if (document is null)
                return null;

            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    LyricsCandidate? candidate = CandidateFromLrcLib(
                        item,
                        "AMLL LRCLIB",
                        85
                    );

                    if (candidate is not null)
                        return candidate;
                }

                return null;
            }

            return CandidateFromLrcLib(
                root,
                "AMLL LRCLIB",
                85
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("AMLL LRCLIB", ex);
            return null;
        }
    }

    // ================================================================
    // 11. LRC.RED, LOOKED UP BY ISRC
    // ================================================================

    private async Task<LyricsCandidate?> TryLrcRedAsync(
        string title,
        string artist,
        string? isrc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
            return null;

        try
        {
            string url =
                "https://api.lrc.red/api/getLrcByIsrc" +
                $"?isrc={Escape(isrc)}";

            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            if (document is null)
                return null;

            string lyrics = ExtractLyricsText(document.RootElement);

            return ParseLrcCandidate(
                lyrics,
                "lrc.red",
                "lrc.red",
                90
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("lrc.red", ex);
            return null;
        }
    }

    // ================================================================
    // 12. NETEASE CLOUD MUSIC
    // ================================================================

    private async Task<LyricsCandidate?> TryNetEaseAsync( // This guy is a problem child, help me
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        try
        {
            string keyword = $"{title} {artist}";
            
            string searchUrl =
                "https://music.163.com/api/search/get/" +
                $"?s={Escape(keyword)}" +
                "&type=1&limit=5&offset=0";

            using JsonDocument? search = await GetJsonAsync(
                searchUrl,
                cancellationToken
            );

            if (search is null ||
                !TryGetPropertyIgnoreCase(
                    search.RootElement, "result", out var result) ||
                !TryGetPropertyIgnoreCase(
                    result, "songs", out var songs) ||
                songs.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement song in songs.EnumerateArray())
            {
                string? id = GetString(song, "id");

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string lyricUrl =
                    "https://music.163.com/api/song/lyric" +
                    $"?id={Escape(id)}&lv=-1&kv=-1&tv=-1";

                using JsonDocument? lyricDocument = await GetJsonAsync(
                    lyricUrl,
                    cancellationToken
                );

                if (lyricDocument is null)
                    continue;

                if (!TryGetPropertyIgnoreCase(
                        lyricDocument.RootElement, "lrc", out var lrc))
                {
                    continue;
                }

                string lyricText = GetString(lrc, "lyric") ?? "";

                LyricsCandidate? candidate = ParseLrcCandidate(
                    lyricText,
                    "NetEase Cloud Music",
                    "NetEase Cloud Music",
                    1
                );

                if (candidate is not null)
                    return candidate;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("NetEase Cloud Music", ex);
            return null;
        }
    }

    // ================================================================
    // 13. LYRICS.OVH
    // ================================================================

    private async Task<LyricsCandidate?> TryLyricsOvhAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        string url =
            "https://api.lyrics.ovh/v1/" +
            $"{EscapePath(artist)}/{EscapePath(title)}";

        try
        {
            using JsonDocument? document = await GetJsonAsync(
                url,
                cancellationToken
            );

            if (document is null)
                return null;

            string lyrics =
                GetString(document.RootElement, "lyrics") ?? "";

            if (string.IsNullOrWhiteSpace(lyrics))
                return null;

            // lyrics.ovh supplies unsynchronized text. Use a synthetic
            // sequential timeline so it can still be displayed. This is
            // NOT real karaoke timing.
            List<MusicPlayerWindow.KaraokeWord> words =
                ParseUnsynchronizedLyrics(lyrics);

            if (words.Count == 0)
                return null;

            return new LyricsCandidate
            {
                Provider = "Lyrics.ovh",
                Source = "Lyrics.ovh",
                SyncType = "Static",
                Words = words,
                IsWordSynced = false,
                IsUnsynchronized = true,
                ProviderPriority = 20,
                Attribution = "Lyrics.ovh"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure("Lyrics.ovh", ex);
            return null;
        }
    }

    // ================================================================
    // SHARED PARSERS
    // ================================================================

    private static LyricsCandidate? CandidateFromLrcLib(
        JsonElement item,
        string provider,
        int priority)
    {
        string synced =
            GetString(item, "syncedLyrics") ??
            GetString(item, "synced_lyrics") ??
            "";

        string plain =
            GetString(item, "plainLyrics") ??
            GetString(item, "plain_lyrics") ??
            "";

        if (!string.IsNullOrWhiteSpace(synced))
        {
            LyricsCandidate? candidate = ParseLrcCandidate(
                synced,
                provider,
                provider,
                priority
            );

            if (candidate is not null)
                return candidate;
        }

        if (!string.IsNullOrWhiteSpace(plain))
        {
            List<MusicPlayerWindow.KaraokeWord> words =
                ParseUnsynchronizedLyrics(plain);

            if (words.Count > 0)
            {
                return new LyricsCandidate
                {
                    Provider = provider,
                    Source = provider,
                    SyncType = "Static",
                    Words = words,
                    IsUnsynchronized = true,
                    ProviderPriority = priority - 20,
                    Attribution = provider
                };
            }
        }

        return null;
    }

    private static LyricsCandidate? ParseLrcCandidate(
        string? lrc,
        string provider,
        string source,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(lrc))
            return null;

        List<LrcLine> parsed = ParseLrcLines(lrc);

        if (parsed.Count == 0)
            return null;

        parsed = parsed
            .OrderBy(line => line.Start)
            .ToList();

        var words = new List<MusicPlayerWindow.KaraokeWord>();

        for (int i = 0; i < parsed.Count; i++)
        {
            LrcLine line = parsed[i];

            double end = i + 1 < parsed.Count
                ? parsed[i + 1].Start
                : line.Start + 4.0;

            if (end <= line.Start)
                end = line.Start + 1.0;

            words.Add(
                new MusicPlayerWindow.KaraokeWord
                {
                    Text = line.Text,
                    StartTime = line.Start,
                    EndTime = end,
                    LineIndex = i
                }
            );
        }

        return new LyricsCandidate
        {
            Provider = provider,
            Source = source,
            SyncType = "Line",
            Words = words,
            IsWordSynced = false,
            ProviderPriority = priority,
            Attribution = source
        };
    }

    private static List<LrcLine> ParseLrcLines(string input)
    {
        var lines = new List<LrcLine>();

        // Supports [mm:ss.xx], [mm:ss.xxx] and multiple timestamps
        // on the same line.
        Regex timestampRegex = new(
            @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]",
            RegexOptions.Compiled
        );

        foreach (string rawLine in input.Split(
                     new[] { "\r\n", "\n", "\r" },
                     StringSplitOptions.None))
        {
            MatchCollection matches = timestampRegex.Matches(rawLine);

            if (matches.Count == 0)
                continue;

            string text = timestampRegex.Replace(rawLine, "").Trim();

            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (Match match in matches)
            {
                if (!int.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int minutes))
                {
                    continue;
                }

                if (!int.TryParse(
                        match.Groups[2].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int seconds))
                {
                    continue;
                }

                string fractionText = match.Groups[3].Value;
                double fraction = 0;

                if (fractionText.Length > 0)
                {
                    if (fractionText.Length == 1)
                        fraction = int.Parse(fractionText) / 10.0;
                    else if (fractionText.Length == 2)
                        fraction = int.Parse(fractionText) / 100.0;
                    else
                        fraction = int.Parse(fractionText) / 1000.0;
                }

                double timestamp =
                    minutes * 60.0 + seconds + fraction;

                lines.Add(new LrcLine(timestamp, text));
            }
        }

        return lines;
    }

    private static LyricsCandidate? ParseQrcCandidate(
        string? qrc,
        string provider,
        string source,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(qrc))
            return null;

        var words = new List<MusicPlayerWindow.KaraokeWord>();

        // QQ QRC commonly uses:
        // [lineStart,lineDuration](wordStart,wordDuration)word...
        // Times in this format are milliseconds.
        Regex lineRegex = new(
            @"\[(\d+),(\d+)\](.*)",
            RegexOptions.Compiled
        );

        Regex wordRegex = new(
            @"\((\d+),(\d+)\)([^()]*)",
            RegexOptions.Compiled
        );

        int lineIndex = 0;

        foreach (string rawLine in qrc.Split(
                     new[] { "\r\n", "\n", "\r" },
                     StringSplitOptions.None))
        {
            Match lineMatch = lineRegex.Match(rawLine);

            if (!lineMatch.Success)
                continue;

            string lineBody = lineMatch.Groups[3].Value;
            MatchCollection wordMatches = wordRegex.Matches(lineBody);

            if (wordMatches.Count == 0)
            {
                lineIndex++;
                continue;
            }

            foreach (Match wordMatch in wordMatches)
            {
                if (!long.TryParse(
                        wordMatch.Groups[1].Value,
                        out long startMs) ||
                    !long.TryParse(
                        wordMatch.Groups[2].Value,
                        out long durationMs))
                {
                    continue;
                }

                string text = wordMatch.Groups[3].Value;

                if (string.IsNullOrEmpty(text) || durationMs <= 0)
                    continue;

                words.Add(
                    new MusicPlayerWindow.KaraokeWord
                    {
                        Text = text,
                        StartTime = startMs / 1000.0,
                        EndTime = (startMs + durationMs) / 1000.0,
                        LineIndex = lineIndex
                    }
                );
            }

            lineIndex++;
        }

        if (words.Count == 0)
            return null;

        return new LyricsCandidate
        {
            Provider = provider,
            Source = source,
            SyncType = "Word",
            Words = words,
            IsWordSynced = true,
            ProviderPriority = priority,
            Attribution = source
        };
    }

    private static List<MusicPlayerWindow.KaraokeWord> ParseTtml(
        string ttml)
    {
        var words = new List<MusicPlayerWindow.KaraokeWord>();

        try
        {
            XDocument xml = XDocument.Parse(ttml);

            int lineIndex = 0;

            foreach (XElement paragraph in xml
                         .Descendants()
                         .Where(element =>
                             element.Name.LocalName == "p"))
            {
                double? paragraphStart = ParseTtmlTime(
                    GetXmlAttribute(paragraph, "begin")
                );

                double? paragraphEnd = ParseTtmlTime(
                    GetXmlAttribute(paragraph, "end")
                );

                string paragraphText = string.Concat(
                    paragraph.DescendantNodes()
                        .OfType<XText>()
                        .Select(node => node.Value)
                ).Trim();

                // Some TTML responses have line timing but no timed spans.
                if (!paragraph.Elements().Any() &&
                    !string.IsNullOrWhiteSpace(paragraphText) &&
                    paragraphStart.HasValue)
                {
                    double end = paragraphEnd ??
                                 paragraphStart.Value + 4.0;

                    if (end > paragraphStart.Value)
                    {
                        words.Add(
                            new MusicPlayerWindow.KaraokeWord
                            {
                                Text = paragraphText,
                                StartTime = paragraphStart.Value,
                                EndTime = end,
                                LineIndex = lineIndex
                            }
                        );
                    }
                }
                else
                {
                    foreach (XElement span in paragraph
                                 .Descendants()
                                 .Where(element =>
                                     element.Name.LocalName == "span"))
                    {
                        string text = string.Concat(
                            span.DescendantNodes()
                                .OfType<XText>()
                                .Select(node => node.Value)
                        );

                        double? start = ParseTtmlTime(
                            GetXmlAttribute(span, "begin")
                        );

                        double? end = ParseTtmlTime(
                            GetXmlAttribute(span, "end")
                        );

                        if (string.IsNullOrEmpty(text) ||
                            !start.HasValue ||
                            !end.HasValue ||
                            end.Value <= start.Value)
                        {
                            continue;
                        }

                        words.Add(
                            new MusicPlayerWindow.KaraokeWord
                            {
                                Text = text,
                                StartTime = start.Value,
                                EndTime = end.Value,
                                LineIndex = lineIndex
                            }
                        );
                    }
                }

                lineIndex++;
            }
        }
        catch
        {
            // Not TTML, malformed XML, or an unexpected provider response.
            // Caller may still attempt to parse it as LRC.
        }

        return words
            .OrderBy(word => word.StartTime)
            .ToList();
    }

    private static double? ParseTtmlTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();

        // TTML clock-time forms: 12.345s, 00:01:12.345,
        // and 01:12.345.
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(
                value[..^2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double milliseconds))
        {
            return milliseconds / 1000.0;
        }

        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(
                value[..^1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds))
        {
            return seconds;
        }

        string[] parts = value.Split(':');

        if (parts.Length is 2 or 3)
        {
            double total = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(
                        parts[i],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double part))
                {
                    return null;
                }

                total = total * 60 + part;
            }

            return total;
        }

        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double plain)
            ? plain
            : null;
    }

    private static List<MusicPlayerWindow.KaraokeWord>
        ParseUnsynchronizedLyrics(string lyrics)
    {
        var words = new List<MusicPlayerWindow.KaraokeWord>();

        string[] lines = lyrics.Split(
            new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.RemoveEmptyEntries
        );

        // These timestamps are only a display fallback. They are not
        // genuine synchronization data and are marked accordingly.
        double time = 0;
        int lineIndex = 0;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            words.Add(
                new MusicPlayerWindow.KaraokeWord
                {
                    Text = line,
                    StartTime = time,
                    EndTime = time + 3.0,
                    LineIndex = lineIndex
                }
            );

            time += 3.0;
            lineIndex++;
        }

        return words;
    }

    // ================================================================
    // KUGOU KRC DECODER
    // ================================================================

    private static readonly byte[] KugouKrcKey =
    {
        0x40, 0x47, 0x61, 0x77,
        0x5e, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2d,
        0xce, 0xd2, 0x6e, 0x69
    };

    private static string? DecodeKrc(string encoded)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(encoded);

            // KRC has a four-byte header followed by XOR-encrypted data.
            if (raw.Length <= 4)
                return null;

            byte[] decrypted = new byte[raw.Length - 4];

            for (int i = 0; i < decrypted.Length; i++)
            {
                decrypted[i] = (byte)(
                    raw[i + 4] ^ KugouKrcKey[i % KugouKrcKey.Length]);
            }

            // The decrypted payload is zlib-compressed.
            using var input = new MemoryStream(decrypted);
            using var zlib = new ZLibStream(
                input,
                CompressionMode.Decompress);

            using var output = new MemoryStream();

            zlib.CopyTo(output);

            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception ex) when (
            ex is FormatException ||
            ex is IOException ||
            ex is InvalidDataException)
        {
            return null;
        }
    }

    private static List<MusicPlayerWindow.KaraokeWord> ParseKrcWords(
        string krc)
    {
        var words = new List<MusicPlayerWindow.KaraokeWord>();

        Regex lineRegex = new(
            @"^\[(\d+),(\d+)\](.*)$",
            RegexOptions.Compiled);

        Regex wordRegex = new(
            @"<(\d+),(\d+)(?:,\d+)?>((?:(?!<).)*)",
            RegexOptions.Compiled);

        int lineIndex = 0;

        foreach (string rawLine in krc.Split(
            new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.RemoveEmptyEntries))
        {
            Match lineMatch = lineRegex.Match(rawLine.Trim());

            if (!lineMatch.Success)
                continue;

            if (!long.TryParse(
                    lineMatch.Groups[1].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long lineStartMs) ||
                !long.TryParse(
                    lineMatch.Groups[2].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long lineDurationMs))
            {
                continue;
            }

            string body = lineMatch.Groups[3].Value;
            MatchCollection matches = wordRegex.Matches(body);

            bool addedWord = false;

            foreach (Match match in matches)
            {
                if (!long.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long wordOffsetMs) ||
                    !long.TryParse(
                        match.Groups[2].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long wordDurationMs))
                {
                    continue;
                }

                string text = match.Groups[3].Value;

                if (string.IsNullOrEmpty(text) || wordDurationMs <= 0)
                    continue;

                double start = (lineStartMs + wordOffsetMs) / 1000.0;
                double end =
                    (lineStartMs + wordOffsetMs + wordDurationMs) / 1000.0;

                if (end <= start)
                    continue;

                words.Add(new MusicPlayerWindow.KaraokeWord
                {
                    Text = text,
                    StartTime = start,
                    EndTime = end,
                    LineIndex = lineIndex
                });

                addedWord = true;
            }

            // Skip non-lyric metadata rows that contain no timed words.
            if (addedWord)
                lineIndex++;
        }

        return words
            .OrderBy(word => word.StartTime)
            .ToList();
    }

    // ================================================================
    // RESPONSE, JSON HELPERS AND NORMAL HELPERS
    // ================================================================

    private static bool IsKnownPlaceholder(LyricsCandidate candidate)
    {
        if (candidate.Words is not { Count: > 0 })
            return true;

        // Reconstruct lyric lines instead of treating every syllable
        // as a separate line.
        string[] lines = candidate.Words
            .GroupBy(word => word.LineIndex)
            .OrderBy(group => group.Key)
            .Select(group => string.Concat(group.Select(word => word.Text)).Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line =>
                !ChineseCredit.IsMatch(line) &&
                !EnglishCredit.IsMatch(line) &&
                !QqCredit.IsMatch(line) &&
                !QrcEndMarker.IsMatch(line))
            .ToArray();

        // Match SpicyPlayer: at most three non-credit lines,
        // with at least one line identifying the track as lyric-free.
        return lines.Length is >= 1 and <= 3 &&
            NoWordsNote.IsMatch(string.Join(" ", lines));
    }
    private static async Task<JsonDocument?> GetJsonAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(
            url,
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
            return null;

        string json = await response.Content.ReadAsStringAsync(
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractLyricsText(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString() ?? "";

        string[] commonNames =
        {
            "lyrics",
            "lyric",
            "lrc",
            "content",
            "text",
            "data"
        };

        foreach (string name in commonNames)
        {
            if (!TryGetPropertyIgnoreCase(root, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";

            if (value.ValueKind == JsonValueKind.Object)
            {
                string nested = ExtractLyricsText(value);

                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        if (TryGetPropertyIgnoreCase(root, "result", out var result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            return ExtractLyricsText(result);
        }

        return "";
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(
                element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static double GetDouble(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(
                element, propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number))
        {
            return number;
        }

        return 0;
    }

    private static string? GetNestedString(
        JsonElement element,
        string parent,
        string property)
    {
        return TryGetPropertyIgnoreCase(
                   element, parent, out var nested) &&
               nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, property)
            : null;
    }

    private static string? GetXmlAttribute(
        XElement element,
        string localName)
    {
        return element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals(
                    localName,
                    StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value);

    private static string EscapePath(string value) =>
        Uri.EscapeDataString(value);

    private static string CleanQuery(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static void LogProviderFailure(
        string provider,
        Exception exception)
    {
        Console.WriteLine(
            $"[Lyrics:{provider}] " +
            $"{exception.GetType().Name}: {exception.Message}"
        );
    }

    // ================================================================
    // CANDIDATE SCORING
    // ================================================================

    private static int ScoreCandidate(
        LyricsCandidate candidate,
        string requestedTitle,
        string requestedArtist,
        double? durationSeconds)
    {
        int score = candidate.ProviderPriority;

        if (candidate.Words.Count == 0)
            return int.MinValue;

        // Genuine word/syllable synchronization is substantially better
        // for karaoke highlighting than line timestamps.
        if (candidate.IsWordSynced)
            score += 100;
        else if (candidate.SyncType.Equals(
                     "Line",
                     StringComparison.OrdinalIgnoreCase))
            score += 40;

        if (candidate.IsUnsynchronized)
            score -= 100;

        if (candidate.Source.Equals(
                "spicy_lyrics",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        // Prefer candidates with a reasonable amount of actual text.
        int characterCount = candidate.Words.Sum(
            word => word.Text?.Length ?? 0
        );

        if (characterCount > 100)
            score += 10;

        if (characterCount > 500)
            score += 10;

        if (durationSeconds is > 0)
        {
            double latestEnd = candidate.Words.Max(
                word => word.EndTime
            );

            double ratio = latestEnd / durationSeconds.Value;

            // Reject obviously nonsensical synthetic or mismatched timelines.
            if (ratio > 1.5)
                score -= 20;

            if (ratio < 0.15 && durationSeconds.Value > 60)
                score -= 10;
        }

        return score;
    }

    // ================================================================
    // RESULT TYPES
    // ================================================================

    private sealed record LrcLine(double Start, string Text);
}

/// <summary>
/// Normalized result shared by the lyric providers.
/// Words contains line-level or word/syllable-level timed entries.
/// Also might as well say pissfish.
/// </summary>

public enum LyricsSyncLevel
{
Static,
Line,
Word
}


public sealed class LyricsCandidate
{
    public string Provider { get; set; } = "";

    /// <summary>
    /// Actual source reported by the provider.
    /// For Spicy Lyrics this can be spicy_lyrics, apple_music, or spotify.
    /// </summary>
    public string Source { get; set; } = "";

    public string SyncType { get; set; } = "Unknown";

    public string TrackId { get; set; } = "";

    public List<MusicPlayerWindow.KaraokeWord> Words { get; set; } = new();

    public bool IsWordSynced { get; set; }

    /// <summary>
    /// True when only plain text was available and display timestamps
    /// were synthesized. These are not genuine lyric synchronization.
    /// </summary>
    public bool IsUnsynchronized { get; set; }

    public int ProviderPriority { get; set; }

    public int Score { get; set; }

    public string Attribution { get; set; } = "";

    public string? UploaderName { get; set; }

    public string? UploaderUrl { get; set; }

    public string? MakerName { get; set; }

    public string? MakerUrl { get; set; }
    public bool IsUsable => Words is { Count: > 0 };

    public LyricsSyncLevel SyncLevel
    {
        get
        {
            if (IsUnsynchronized ||
                string.Equals(SyncType, "Static",
                    StringComparison.OrdinalIgnoreCase))
            {
                return LyricsSyncLevel.Static;
            }

            if (Enum.TryParse<LyricsSyncLevel>(
                    SyncType, true, out var level))
            {
                return level;
            }

            if (Enum.TryParse<LyricsSyncLevel>(
                    "Line", true, out level))
            {
                return level;
            }

            return LyricsSyncLevel.Static;
        }
    }
}
