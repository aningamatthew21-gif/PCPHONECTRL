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
                string term = Uri.EscapeDataString($"{artist} {title}".Trim());
                var json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=1");
                using var doc = JsonDocument.Parse(json);
                var results = doc.RootElement.GetProperty("results");
                if (results.GetArrayLength() > 0 && results[0].TryGetProperty("artworkUrl100", out var art))
                {
                    var url = (art.GetString() ?? "").Replace("100x100bb", "300x300bb");
                    if (url.Length > 0) image = await Http.GetByteArrayAsync(url);
                }
            }
            catch (Exception ex)
            {
                Log.Info("Artwork lookup failed: " + ex.Message);
                return null; // don't cache failures: try again next time
            }

            lock (Cache) Cache[key] = image;
            return image;
        }
    }
}
