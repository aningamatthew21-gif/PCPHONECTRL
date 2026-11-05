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
        /// <summary>"Left" or "Right" screen edge the widget is docked to.</summary>
        public string Edge { get; set; } = "Right";
        /// <summary>X coordinate (DIPs) of the edge the widget is docked to. null = primary screen.</summary>
        public double? EdgeX { get; set; }
        /// <summary>Vertical centre of the widget (DIPs). null = middle of the screen.</summary>
        public double? CenterY { get; set; }

        // ---- Feed scrolling (TikTok / Reels / Shorts) ----
        /// <summary>Mouse-wheel notches sent for one "next/previous video" click.</summary>
        public int ScrollNotches { get; set; } = 1;
        /// <summary>Flip this if ▼ goes to the previous video instead of the next one.</summary>
        public bool InvertScroll { get; set; } = false;

        // ---- Lock ----
        /// <summary>"MouseButton" (AssistiveTouch custom button → Lock Screen) or "PowerKey" (experimental).</summary>
        public string LockMethod { get; set; } = "MouseButton";
        /// <summary>Mouse button sent for lock: 3 = middle, 4, 5 = extra buttons. Must match your AssistiveTouch mapping.</summary>
        public int LockMouseButton { get; set; } = 3;

        // ---- "Center pointer" helper ----
        /// <summary>Pointer movement (in mouse counts) from the top-left corner to the middle of the screen.</summary>
        public int CenterOffsetX { get; set; } = 200;
        public int CenterOffsetY { get; set; } = 400;

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
            [nameof(PhoneAction.PlayPause)] = "Ctrl+Alt+Space",
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
                    if (s != null) return s;
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
