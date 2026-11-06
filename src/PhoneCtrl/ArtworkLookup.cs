using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace PhoneCtrl
{
    /// <summary>
    /// Finds album artwork for a song. The iPhone doesn't send artwork over Bluetooth, so this asks Apple's public
    /// iTunes Search API (no account or key). It sends the song title and artist to itunes.apple.com.
    /// Turn it off with "LookUpAlbumArt": false in the settings file.
    /// </summary>
    internal static class ArtworkLookup
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly Dictionary<string, byte[]?> Cache = new Dictionary<string, byte[]?>();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneCtrl");
            return c;
        }

        /// <summary>Returns the image bytes (JPEG/PNG) or null when nothing was found.</summary>
        public static async Task<byte[]?> FindAsync(string artist, string title, string album)
        {
            string key = $"{artist}\n{title}".ToLowerInvariant();
            lock (Cache)
                if (Cache.TryGetValue(key, out var cached)) return cached;

            byte[]? image = null;
            try
            {
                string? url = await BestMatchAsync(artist, title, album);
                if (url != null) image = await Http.GetByteArrayAsync(url);
            }
            catch (Exception ex)
            {
                Log.Info("Artwork lookup failed: " + ex.Message);
                return null; // don't cache failures: try again next time
            }

            lock (Cache) Cache[key] = image;
            return image;
        }

        /// <summary>
        /// Searches the iTunes catalogue (in this PC's country first, then the US) and picks the result whose
        /// album, title and artist best match what the iPhone reports, rather than just the first hit.
        /// </summary>
        private static async Task<string?> BestMatchAsync(string artist, string title, string album)
        {
            string country = "US";
            try { country = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { }

            string? best = null;
            int bestScore = 0;
            foreach (var store in country == "US" ? new[] { "US" } : new[] { country, "US" })
            {
                foreach (var term in new[] { $"{artist} {title}", $"{artist} {album}" })
                {
                    string url = "https://itunes.apple.com/search?media=music&entity=song&limit=25" +
                        $"&country={store}&term={Uri.EscapeDataString(term.Trim())}";
                    string json;
                    try { json = await Http.GetStringAsync(url); }
                    catch (HttpRequestException) { continue; }

                    using var doc = JsonDocument.Parse(json);
                    foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                    {
                        int score = 0;
                        string rTitle = Text(r, "trackName"), rArtist = Text(r, "artistName"), rAlbum = Text(r, "collectionName");
                        if (Same(rTitle, title)) score += 4; else if (Contains(rTitle, title)) score += 2;
                        if (Same(rArtist, artist)) score += 3; else if (Contains(rArtist, artist)) score += 1;
                        if (Same(rAlbum, album)) score += 5; else if (Contains(rAlbum, album)) score += 2;
                        if (score > bestScore && r.TryGetProperty("artworkUrl100", out var art))
                        {
                            bestScore = score;
                            best = (art.GetString() ?? "").Replace("100x100bb", "300x300bb");
                        }
                    }
                    if (bestScore >= 12) return best; // title + artist + album all match exactly
                }
            }
            // Require at least a title or album match so we don't show an unrelated cover.
            return bestScore >= 4 ? best : null;
        }

        private static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

        private static string Normalize(string s)
        {
            s = s.ToLowerInvariant();
            foreach (var junk in new[] { " - single", " - ep", "(feat.", "[feat." })
            {
                int i = s.IndexOf(junk, StringComparison.Ordinal);
                if (i > 0) s = s.Substring(0, i);
            }
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        private static bool Same(string a, string b) => Normalize(a).Length > 0 && Normalize(a) == Normalize(b);

        private static bool Contains(string a, string b)
        {
            string na = Normalize(a), nb = Normalize(b);
            return na.Length > 0 && nb.Length > 0 && (na.Contains(nb) || nb.Contains(na));
        }
    }
}
