using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PhoneCtrl
{
    /// <summary>
    /// Self-updater. Checks the repository's "latest-build" GitHub release for a newer version,
    /// downloads the new PhoneCtrl.exe next to the running one, swaps them and restarts.
    /// </summary>
    internal sealed class Updater
    {
        private const string ReleaseApi =
            "https://api.github.com/repos/aningamatthew21-gif/PCPHONECTRL/releases/tags/latest-build";

        private static readonly HttpClient Http = CreateClient();

        public sealed class UpdateInfo
        {
            public UpdateInfo(Version version, string downloadUrl)
            {
                Version = version;
                DownloadUrl = downloadUrl;
            }

            public Version Version { get; }
            public string DownloadUrl { get; }
        }

        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PhoneCtrl.exe");
        private static string OldExePath => Path.ChangeExtension(ExePath, ".old.exe");
        private static string NewExePath => Path.ChangeExtension(ExePath, ".new.exe");

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneCtrl-Updater");
            return c;
        }

        /// <summary>True when the last check couldn't reach GitHub (as opposed to "no update").</summary>
        public bool LastCheckFailed { get; private set; }

        /// <summary>Returns update info when GitHub has a newer version than this one, otherwise null.</summary>
        public async Task<UpdateInfo?> CheckAsync()
        {
            LastCheckFailed = true;
            try
            {
                using var response = await Http.GetAsync(ReleaseApi);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Info($"Update check: GitHub answered {(int)response.StatusCode}");
                    return null;
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                // Release title looks like "PhoneCtrl 0.3.12"
                var title = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var match = Regex.Match(title, @"(\d+)\.(\d+)\.(\d+)");
                if (!match.Success) return null;
                var latest = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value));

                string? url = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    if (string.Equals(asset.GetProperty("name").GetString(), "PhoneCtrl.exe", StringComparison.OrdinalIgnoreCase))
                        url = asset.GetProperty("browser_download_url").GetString();
                }

                Log.Info($"Update check: current {CurrentVersion}, latest {latest}");
                LastCheckFailed = false;
                return url != null && latest > CurrentVersion ? new UpdateInfo(latest, url) : null;
            }
            catch (Exception ex)
            {
                Log.Error("Update check failed", ex);
                return null;
            }
        }

        /// <summary>
        /// Downloads the update and replaces the running exe. Windows lets a running exe be renamed
        /// (but not overwritten), so: current → .old.exe, downloaded → current. Returns the path to start.
        /// </summary>
        public async Task<string> DownloadAndSwapAsync(UpdateInfo update, IProgress<int> progress)
        {
            var exe = ExePath;
            var tmp = NewExePath;

            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(tmp);
                var buffer = new byte[81920];
                long done = 0;
                int read, lastPercent = -1;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent != lastPercent) progress.Report(lastPercent = percent);
                    }
                }
            }

            if (new FileInfo(tmp).Length < 1_000_000)
                throw new InvalidOperationException("The downloaded file is too small, so the download probably failed.");

            if (File.Exists(OldExePath)) File.Delete(OldExePath);
            File.Move(exe, OldExePath);
            try
            {
                File.Move(tmp, exe);
            }
            catch
            {
                File.Move(OldExePath, exe); // roll back
                throw;
            }
            Log.Info($"Installed update {update.Version}");
            return exe;
        }

        /// <summary>Starts the new version, which waits for this process to exit first.</summary>
        public static void Restart(string exe)
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                Arguments = $"--after-update {Environment.ProcessId}",
            });
        }

        /// <summary>Called at startup: removes the previous version's leftover file.</summary>
        public static void CleanUp()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (File.Exists(OldExePath)) File.Delete(OldExePath);
                    if (File.Exists(NewExePath)) File.Delete(NewExePath);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(500);
                }
            }
        }

        /// <summary>When started by an update, wait until the old process has exited.</summary>
        public static void WaitForPreviousInstance(string[] args)
        {
            int i = Array.IndexOf(args, "--after-update");
            if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(15000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }
}
