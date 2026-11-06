using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneCtrl
{
    /// <summary>
    /// User settings, stored as JSON in %APPDATA%\PhoneCtrl\settings.json.
    /// Open it from the tray icon menu → "Open settings file", edit, then "Reload settings".
    /// </summary>
    public sealed class AppSettings
    {
        // ---- Widget position ----
        /// <summary>Screen edge the widget is docked to: "Left", "Right", "Top" or "Bottom".</summary>
        public string Edge { get; set; } = "Right";
        /// <summary>A point (WPF units) on the monitor the widget lives on; also where along the edge it sits.</summary>
        public double? AnchorX { get; set; }
        public double? AnchorY { get; set; }

        // ---- Feed scrolling (TikTok / Reels / Shorts) ----
        /// <summary>"Swipe" (drag with the pointer, like a finger) or "Wheel" (mouse-wheel scroll).</summary>
        public string ScrollMethod { get; set; } = "Swipe";
        /// <summary>How far one swipe drags the pointer (mouse counts). Raise it if a swipe doesn't change the video.</summary>
        public int SwipeDistance { get; set; } = 400;
        /// <summary>Number of steps the swipe is split into. Fewer = faster flick.</summary>
        public int SwipeSteps { get; set; } = 8;
        /// <summary>Mouse-wheel notches per click (only for ScrollMethod "Wheel").</summary>
        public int ScrollNotches { get; set; } = 1;
        /// <summary>Flip this if ▼ goes to the previous video instead of the next one.</summary>
        public bool InvertScroll { get; set; } = false;

        // ---- Mouse on iPhone ----
        /// <summary>Shortcut that brings the mouse and keyboard back to the PC.</summary>
        public string CaptureExitKey { get; set; } = "Ctrl+Alt+Q";
        /// <summary>Holding the right mouse button this many seconds also brings the mouse back (0 = off).</summary>
        public double CaptureExitRightHoldSeconds { get; set; } = 2;

        // ---- Lock ----
        /// <summary>"MouseButton" (AssistiveTouch custom button → Lock Screen) or "PowerKey" (experimental).</summary>
        public string LockMethod { get; set; } = "MouseButton";
        /// <summary>Mouse button sent for lock: 3 = middle, 4, 5 = extra buttons. Must match your AssistiveTouch mapping.</summary>
        public int LockMouseButton { get; set; } = 3;

        // ---- Where swipes start ----
        /// <summary>
        /// Before each swipe the phone pointer is pushed to the top of the screen, then moved this far down
        /// (mouse counts, sent slowly so iOS doesn't accelerate it). Swipes must start mid-screen: a swipe
        /// up from the bottom edge is the iPhone "go Home" gesture. Raise it if swipes start too high,
        /// lower it if they still go Home. Set 0 to swipe from wherever the pointer is.
        /// </summary>
        public int SwipeStartFromTop { get; set; } = 350;

        // ---- Now playing ----
        /// <summary>Show the song playing on the iPhone (read over Bluetooth with Apple Media Service).</summary>
        public bool ShowNowPlaying { get; set; } = true;
        /// <summary>
        /// Look up album artwork on Apple's iTunes Search (sends the song title and artist to itunes.apple.com).
        /// The iPhone doesn't send artwork over Bluetooth.
        /// </summary>
        public bool LookUpAlbumArt { get; set; } = true;

        // ---- Bluetooth ----
        public int ReportIntervalMs { get; set; } = 15;

        // ---- Global keyboard shortcuts (work even when the widget is hidden) ----
        public Dictionary<string, string> HotKeys { get; set; } = new Dictionary<string, string>
        {
            [nameof(PhoneAction.Lock)] = "Ctrl+Alt+L",
            [nameof(PhoneAction.NextVideo)] = "Ctrl+Alt+PageDown",
            [nameof(PhoneAction.PreviousVideo)] = "Ctrl+Alt+PageUp",
            [nameof(PhoneAction.NextTrack)] = "Ctrl+Alt+End",
            [nameof(PhoneAction.PreviousTrack)] = "Ctrl+Alt+Home",
            [nameof(PhoneAction.PlayPause)] = "Ctrl+Alt+P",
            [nameof(PhoneAction.MouseToPhone)] = "Ctrl+Alt+M",
            [nameof(PhoneAction.VolumeUp)] = "Ctrl+Alt+Add",
            [nameof(PhoneAction.VolumeDown)] = "Ctrl+Alt+Subtract",
        };

        // ------------------------------------------------------------------ persistence

        public static string Folder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneCtrl");

        public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                    if (s != null)
                    {
                        s.Upgrade();
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read settings, using defaults", ex);
            }
            var defaults = new AppSettings();
            defaults.Save();
            return defaults;
        }

        /// <summary>Adds shortcuts introduced in newer versions and fixes known-bad old defaults.</summary>
        private void Upgrade()
        {
            var defaults = new AppSettings().HotKeys;
            foreach (var pair in defaults)
                if (!HotKeys.ContainsKey(pair.Key)) HotKeys[pair.Key] = pair.Value;
            // Ctrl+Alt+Space clashes with other apps on many PCs.
            if (HotKeys.TryGetValue(nameof(PhoneAction.PlayPause), out var pp) && pp == "Ctrl+Alt+Space")
                HotKeys[nameof(PhoneAction.PlayPause)] = "Ctrl+Alt+P";
            Save();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch (Exception ex)
            {
                Log.Error("Could not save settings", ex);
            }
        }
    }
}
