using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ubar3;

internal static class Kuwo
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly byte[] Key = Encoding.UTF8.GetBytes("yeelion");

    private const int MaxCandidates = 3;

    private static readonly Regex LineRegex = new(
        @"^\[(\d+):(\d+)(?:\.(\d+))?\]",
        RegexOptions.Compiled);

    private static readonly Regex TokenRegex = new(
        @"<(-?\d+),(-?\d+)>([^<]*)",
        RegexOptions.Compiled);

    private static readonly Regex CutRegex = new(
        @"伴奏|片段|现场|翻唱|铃声|DJ版|试听",
        RegexOptions.Compiled);

    private static readonly Regex TranslationNoteRegex = new(
        @"歌词翻译由.{0,24}提供",
        RegexOptions.Compiled);

    private sealed record Song( //After all, why not... why not throw my JBL GO+ at my 5 year old sister :)
        string Title,
        string Artist,
        int Duration,
        string Rid);

    private sealed record TimedLine(
        double Start,
        string Text);

    public static async Task<LyricsCandidate?> FetchAsync( // One of the more stable guys....... if (you.MakeItWork() == true)
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            List<Song> songs = await SearchAsync(
                title,
                artist,
                cancellationToken);

            Console.WriteLine($"[Kuwo DEBUG] Search for: {artist} - {title}");
            Console.WriteLine($"[Kuwo DEBUG] Search returned {songs.Count} matching songs.");

            LyricsCandidate? lineFallback = null;

            foreach (Song song in songs.Take(MaxCandidates))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    byte[] response = await GetBytesAsync(
                        BuildLyricsUrl(song.Rid),
                        cancellationToken);

                    string text = DecodeResponse(response);

                    // Prefer genuine word-timed LRCX.
                    LyricsCandidate? wordCandidate =
                        ParseWordTimedLyrics(text);

                    if (wordCandidate is not null){
                        Console.WriteLine(
                            $"[Kuwo DEBUG] WORD-SYNC SUCCESS: {song.Title}");
                        return wordCandidate;}  

                    Console.WriteLine("Kuwo Debug found nothing except a paperclip and some rusty nails :|");
                    // Retain line-synced lyrics as a fallback.
                    LyricsCandidate? lineCandidate =
                        ParseLineTimedLyrics(text);

                    if (lineCandidate is not null)
                    {
                        Console.WriteLine(
                            $"[Kuwo DEBUG] LINE-SYNC SUCCESS: {song.Title}");
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[Kuwo DEBUG] No usable lyrics for: {song.Title}");
                    }

                    lineFallback ??= lineCandidate;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Kuwo DEBUG] Failed on '{song.Title}' by '{song.Artist}' " +
                        $"(RID: {song.Rid}): {ex.GetType().Name}: {ex.Message}");
                    // One bad track must not prevent trying another copy.
                }
            }

            return lineFallback;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A provider failure must not break the overall lyrics lookup.
            Console.WriteLine(
                $"[Kuwo DEBUG] PROVIDER FAILURE: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<List<Song>> SearchAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["all"] = $"{artist} {title}",
            ["ft"] = "music",
            ["client"] = "kt",
            ["cluster"] = "0",
            ["pn"] = "0",
            ["rn"] = "8",
            ["rformat"] = "json",
            ["encoding"] = "utf8",
            ["vipver"] = "1",
            ["strategy"] = "2012",
            ["mobi"] = "1",
            ["issubtitle"] = "1",
            ["show_copyright_off"] = "1"
        };

        string query = string.Join("&", parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}=" +
            $"{Uri.EscapeDataString(p.Value)}"));

        string url = $"https://search.kuwo.cn/r.s?{query}";

        byte[] bytes = await GetBytesAsync(url, cancellationToken);
        string raw = Encoding.UTF8.GetString(bytes);

        using JsonDocument document = JsonDocument.Parse(
            NormalizeSingleQuotedJson(raw));

        if (!document.RootElement.TryGetProperty(
                "abslist", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return new List<Song>();
        }

        var songs = new List<Song>();

        foreach (JsonElement item in list.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            string songTitle =
                GetProperty(item, "SONGNAME");

            if (string.IsNullOrWhiteSpace(songTitle))
                songTitle = GetProperty(item, "NAME");

            string songArtist = GetProperty(item, "ARTIST");

            string rid = GetProperty(item, "MUSICRID");

            if (string.IsNullOrWhiteSpace(rid))
                rid = GetProperty(item, "DC_TARGETID");

            if (string.IsNullOrWhiteSpace(rid))
                continue;

            if (!rid.StartsWith(
                    "MUSIC_", StringComparison.OrdinalIgnoreCase))
            {
                rid = "MUSIC_" + rid;
            }

            int duration = int.TryParse(
                GetProperty(item, "DURATION"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedDuration)
                ? parsedDuration
                : 0;

            if (!TitlesMatch(songTitle, title))
                continue;

            if (!ArtistsMatch(songArtist, artist))
                continue;

            if (CutRegex.IsMatch(songTitle) &&
                !CutRegex.IsMatch(title))
            {
                continue;
            }

            songs.Add(new Song(
                songTitle,
                songArtist,
                duration,
                rid));
        }

        return songs;
    }

    private static string BuildLyricsUrl(string rid)
    {
        // This is the request format used by the original Spicy Player
        // Kuwo provider. The query itself is XOR-encoded before Base64.
        string rawQuery =
            $"user=12345,web,web,web&requester=localhost&req=1&rid={rid}&lrcx=1";

        byte[] bytes = Encoding.UTF8.GetBytes(rawQuery);
        XorInPlace(bytes);

        string encoded = Convert.ToBase64String(bytes);

        // Escape the Base64 query value so '+' isn't interpreted as a
        // space by query-string parsers.
        return "https://newlyric.kuwo.cn/newlyric.lrc?" +
               Uri.EscapeDataString(encoded);
    }

    private static async Task<byte[]> GetBytesAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, url);

        request.Headers.TryAddWithoutValidation(
            "Referer", "https://www.kuwo.cn/");

        using HttpResponseMessage response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(
            cancellationToken);
    }

    private static string DecodeResponse(byte[] response)
    {
        // Kuwo returns a small header followed by compressed content.
        byte[] separator = { 13, 10, 13, 10 };
        int split = IndexOf(response, separator);

        if (split < 0)
            throw new InvalidDataException(
                "Kuwo response has no header/body separator.");

        string headers = Encoding.ASCII.GetString(
            response, 0, split);

        if (!headers.StartsWith(
                "tp=content", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Unexpected Kuwo lyrics response.");
        }

        byte[] compressed = response
            .Skip(split + separator.Length)
            .ToArray();

        byte[] inflated;

        // Java's InflaterInputStream, used in the original Kotlin code,
        // expects a zlib-wrapped DEFLATE stream. ZLibStream is the
        // corresponding .NET implementation.
        using (var input = new MemoryStream(compressed))
        using (var zlib = new ZLibStream(
                   input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            zlib.CopyTo(output);
            inflated = output.ToArray();
        }

        string raw = Encoding.ASCII.GetString(inflated).TrimStart('\uFEFF');

        // The original implementation accepts plain LRCX or a
        // Base64-encoded, XOR-obfuscated payload.
        if (raw.TrimStart().StartsWith(
                "[", StringComparison.Ordinal))
        {
            return Encoding.GetEncoding("GB18030")
                .GetString(inflated)
                .TrimStart('\uFEFF');
        }

        byte[] decoded = Convert.FromBase64String(raw);
        XorInPlace(decoded);

        return Encoding.GetEncoding("GB18030")
            .GetString(decoded)
            .TrimStart('\uFEFF');
    }

    private static LyricsCandidate? ParseWordTimedLyrics(string text)
    {
        Match marker = Regex.Match(
            text, @"\[kuwo:(\d+)\]");

        if (!marker.Success ||
            !int.TryParse(
                marker.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int octalValue))
        {
            return null;
        }

        // Kotlin's toIntOrNull(8) parses the marker as octal.
        string markerDigits = marker.Groups[1].Value;

        try
        {
            octalValue = Convert.ToInt32(markerDigits, 8);
        }
        catch
        {
            Console.WriteLine("And now some Kotlin thingy, idk");
            return null;
        }

        int k1 = octalValue / 10;
        int k2 = octalValue % 10;

        if (k1 == 0 || k2 == 0)
            return null;

        var words = new List<MusicPlayerWindow.KaraokeWord>();
        int lineIndex = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            Match timestamp = LineRegex.Match(line);

            if (!timestamp.Success)
                continue;

            string remainder = line[timestamp.Length..];
            MatchCollection tokens = TokenRegex.Matches(remainder);

            if (tokens.Count == 0)
                continue;

            bool hasWordTiming = tokens
                .Cast<Match>()
                .Any(m =>
                    m.Groups[1].Value != "0" ||
                    m.Groups[2].Value != "0");

            // Kuwo sometimes includes translation lines with <0,0>.
            if (!hasWordTiming)
                continue;

            double lineStart = ParseTimestamp(timestamp);

            var parsed = new List<(
                double Offset,
                double Duration,
                string Text)>();

            foreach (Match token in tokens)
            {
                if (!long.TryParse(
                        token.Groups[1].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long a) ||
                    !long.TryParse(
                        token.Groups[2].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long b))
                {
                    continue;
                }

                double offsetMs =
                    Math.Abs((double)a + b) / (2.0 * k1);

                double durationMs =
                    Math.Abs((double)a - b) / (2.0 * k2);

                string word = token.Groups[3].Value;

                if (string.IsNullOrEmpty(word))
                    continue;

                parsed.Add((
                    offsetMs / 1000.0,
                    durationMs / 1000.0,
                    word));
            }

            if (parsed.Count == 0)
                continue;

            foreach (var token in parsed)
            {
                double start = lineStart + token.Offset;
                double end = start + token.Duration;

                if (end <= start)
                    end = start + 0.05;

                words.Add(new MusicPlayerWindow.KaraokeWord
                {
                    Text = token.Text,
                    StartTime = start,
                    EndTime = end,
                    LineIndex = lineIndex,
                    IsPartOfWord = false
                });
            }

            lineIndex++;
        }

        if (words.Count == 0)
            return null;

        return new LyricsCandidate
        {
            Provider = "Kuwo",
            Attribution = "Kuwo Music",
            ProviderPriority = 255,
            SyncType = "Word",
            IsWordSynced = true,
            Words = words
        };
    }

    private static LyricsCandidate? ParseLineTimedLyrics(string text)
    {
        var lines = new List<TimedLine>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.StartsWith("[kuwo:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[ml:", StringComparison.OrdinalIgnoreCase) ||
                TranslationNoteRegex.IsMatch(line))
            {
                continue;
            }

            Match timestamp = LineRegex.Match(line);

            if (!timestamp.Success)
                continue;

            string content = line[timestamp.Length..].Trim();

            if (string.IsNullOrWhiteSpace(content))
                continue;

            lines.Add(new TimedLine(
                ParseTimestamp(timestamp),
                content));
        }

        if (lines.Count == 0)
            return null;

        var words = new List<MusicPlayerWindow.KaraokeWord>();

        for (int i = 0; i < lines.Count; i++)
        {
            double start = lines[i].Start;

            double end = i + 1 < lines.Count
                ? lines[i + 1].Start
                : start + 4.0;

            if (end <= start)
                end = start + 0.1;

            words.Add(new MusicPlayerWindow.KaraokeWord
            {
                // Keep the entire lyric line as one timed segment.
                // Do not invent word-level timestamps.
                Text = lines[i].Text,
                StartTime = start,
                EndTime = end,
                LineIndex = i,
                IsPartOfWord = false
            });
        }

        return new LyricsCandidate
        {
            Provider = "Kuwo",
            Attribution = "Kuwo Music",
            ProviderPriority = 225,
            SyncType = "Line",
            IsWordSynced = false,
            Words = words
        };

    }

    private static double ParseTimestamp(Match match)
    {
        double minutes = double.Parse(
            match.Groups[1].Value,
            CultureInfo.InvariantCulture);

        double seconds = double.Parse(
            match.Groups[2].Value,
            CultureInfo.InvariantCulture);

        string fraction = match.Groups[3].Success
            ? match.Groups[3].Value
            : "";

        if (fraction.Length > 3)
            fraction = fraction[..3];

        fraction = fraction.PadRight(3, '0');

        double milliseconds = fraction.Length == 0
            ? 0
            : double.Parse(
                fraction,
                CultureInfo.InvariantCulture);

        return minutes * 60 + seconds + milliseconds / 1000.0;
    }

    private static int IndexOf(byte[] source, byte[] needle)
    {
        for (int i = 0; i <= source.Length - needle.Length; i++)
        {
            bool matches = true;

            for (int j = 0; j < needle.Length; j++)
            {
                if (source[i + j] == needle[j])
                    continue;

                matches = false;
                break;
            }

            if (matches)
                return i;
        }

        return -1;
    }

    private static void XorInPlace(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] ^= Key[i % Key.Length];
    }

    private static string GetProperty(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return "";

        string result = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();

        return result
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static bool TitlesMatch(string candidate, string requested)
    {
        return Normalize(candidate) == Normalize(requested);
    }

    private static bool ArtistsMatch(string candidate, string requested)
    {
        string a = Normalize(candidate);
        string b = Normalize(requested);

        return a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(
            value.Normalize(NormalizationForm.FormKC)
                .ToLowerInvariant(),
            @"[^\p{L}\p{N}]",
            "");
    }

    /// <summary>
    /// Converts single-quoted strings in JavaScript-style JSON to JSON
    /// double-quoted strings without changing apostrophes inside strings.
    /// </summary>
    private static string NormalizeSingleQuotedJson(string raw)
    {
        var output = new StringBuilder(raw.Length);
        bool inDoubleQuotedString = false;
        bool inSingleQuotedString = false;
        bool escaped = false;

        foreach (char c in raw)
        {
            if (escaped)
            {
                if (inSingleQuotedString && c == '\'')
                    output.Append('\'');
                else
                    output.Append(c);

                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                output.Append(c);
                escaped = true;
                continue;
            }

            if (inDoubleQuotedString)
            {
                output.Append(c);

                if (c == '"')
                    inDoubleQuotedString = false;

                continue;
            }

            if (inSingleQuotedString)
            {
                if (c == '\'')
                {
                    output.Append('"');
                    inSingleQuotedString = false;
                }
                else if (c == '"')
                {
                    output.Append("\\\"");
                }
                else
                {
                    output.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inDoubleQuotedString = true;
                output.Append(c);
            }
            else if (c == '\'')
            {
                inSingleQuotedString = true;
                output.Append('"');
            }
            else
            {
                output.Append(c);
            }
        }

        return output.ToString();
    }
}