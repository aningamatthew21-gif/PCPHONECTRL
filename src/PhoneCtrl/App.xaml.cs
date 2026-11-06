using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using PhoneCtrl.Ble;

namespace PhoneCtrl
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private HidPeripheral? _phone;
        private WidgetWindow? _widget;
        private TrayIcon? _tray;
        private InputCapture? _capture;
        private CaptureBanner? _banner;
        private readonly Updater _updater = new Updater();
        private Updater.UpdateInfo? _pendingUpdate;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;
        private bool _installing;

        public AppSettings Settings { get; private set; } = new AppSettings();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // After a self-update, the new version waits here for the old one to close.
            Updater.WaitForPreviousInstance(e.Args);

            _singleInstance = new Mutex(true, @"Local\PhoneCtrl.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("PhoneCtrl is already running (look for its icon near the clock).",
                    "PhoneCtrl", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error("Unhandled UI exception", args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Error("Unhandled exception", args.ExceptionObject as Exception);

            Log.Info($"PhoneCtrl {Updater.CurrentVersion} starting");
            Settings = AppSettings.Load();

            _phone = new HidPeripheral { ReportIntervalMs = Settings.ReportIntervalMs };
            var actions = new PhoneActions(_phone, () => Settings);

            _capture = new InputCapture(_phone, Dispatcher);
            _capture.SetExitShortcut(Settings.CaptureExitKey);
            _capture.SetRightHoldToExit(Settings.CaptureExitRightHoldSeconds);
            _banner = new CaptureBanner();
            _capture.ActiveChanged += active =>
            {
                if (active) _banner.ShowFor(ExitHint()); else _banner.Hide();
                _widget?.SetCaptureActive(active);
            };
            // If the phone disconnects while it has the mouse, give the mouse back immediately.
            _phone.StateChanged += (state, _) =>
            {
                if (state != PeripheralState.Connected)
                    Dispatcher.InvokeAsync(() => _capture?.Stop());
            };

            _widget = new WidgetWindow(this, _phone, actions);
            _widget.Show();

            _tray = new TrayIcon(this);

            CloseBleHidIfRunning();
            _ = _phone.StartAsync();

            // Look for updates shortly after start, then every 4 hours.
            System.Threading.Tasks.Task.Run(Updater.CleanUp);
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(4);
                await CheckForUpdatesAsync(userAsked: false);
            };
            _updateTimer.Start();
        }

        /// <summary>BleHid publishes the same Bluetooth service, so the two can't run together.</summary>
        private static void CloseBleHidIfRunning()
        {
            var running = new System.Collections.Generic.List<Process>();
            foreach (var name in new[] { "BleHid.App", "BleHid.Cli", "BleHid" })
                running.AddRange(Process.GetProcessesByName(name));
            if (running.Count == 0) return;

            var answer = MessageBox.Show(
                "BleHid is still running.\n\nPhoneCtrl has its own Bluetooth connection now, and the two can't run " +
                "at the same time (the iPhone would connect to BleHid instead).\n\nClose BleHid now?",
                "PhoneCtrl", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            foreach (var p in running)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not close BleHid", ex);
                }
            }
            Log.Info("Closed BleHid");
        }

        /// <summary>How to get the mouse back, e.g. "hold the right mouse button 2 s or press Ctrl+Alt+Q".</summary>
        public string ExitHint() => Settings.CaptureExitRightHoldSeconds > 0
            ? $"hold the right mouse button {Settings.CaptureExitRightHoldSeconds:0.#} s or press {Settings.CaptureExitKey}"
            : $"press {Settings.CaptureExitKey}";

        /// <summary>Send this PC's mouse and keyboard to the iPhone until the exit shortcut is pressed.</summary>
        public bool StartCapture() => _capture?.Start() == true;

        // ---- updates ----

        /// <summary>
        /// Checks GitHub. Returns true = update ready, false = already up to date, null = couldn't check.
        /// userAsked: from the tray menu (shows a message box when there's nothing new).
        /// fromWidget: the widget shows the result itself, so no message box or notification.
        /// </summary>
        public async System.Threading.Tasks.Task<bool?> CheckForUpdatesAsync(bool userAsked, bool fromWidget = false)
        {
            var update = await _updater.CheckAsync();
            if (update == null)
            {
                if (userAsked && !fromWidget)
                    MessageBox.Show(_updater.LastCheckFailed
                            ? "Couldn't reach GitHub to check for updates. Check your internet connection and try again."
                            : $"You have the latest version ({Updater.CurrentVersion}).",
                        "PhoneCtrl", MessageBoxButton.OK, MessageBoxImage.Information);
                return _updater.LastCheckFailed ? (bool?)null : false;
            }
            bool isNew = _pendingUpdate == null || _pendingUpdate.Version != update.Version;
            _pendingUpdate = update;
            _widget?.ShowUpdateAvailable(update.Version);
            if (!fromWidget && (isNew || userAsked))
                _tray?.Notify("PhoneCtrl update available",
                    $"Version {update.Version} is ready. Open the widget and click the blue Update button.");
            return true;
        }

        public bool HasPendingUpdate => _pendingUpdate != null;

        public async void InstallUpdate()
        {
            if (_pendingUpdate == null || _installing) return;
            _installing = true;
            try
            {
                var progress = new Progress<int>(p => _widget?.ShowUpdateProgress($"Downloading… {p}%"));
                _widget?.ShowUpdateProgress("Downloading…");
                var exe = await _updater.DownloadAndSwapAsync(_pendingUpdate, progress);
                _widget?.ShowUpdateProgress("Restarting…");
                Updater.Restart(exe);
                Quit();
            }
            catch (Exception ex)
            {
                Log.Error("Update failed", ex);
                _installing = false;
                _widget?.ShowUpdateAvailable(_pendingUpdate.Version);
                MessageBox.Show("The update couldn't be installed:\n\n" + ex.Message +
                    "\n\nYou can download it manually from the GitHub Releases page.",
                    "PhoneCtrl", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---- start with Windows / shortcut ----

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static string ExePath => Environment.ProcessPath ?? "";

        public bool StartsWithWindows
        {
            get
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue("PhoneCtrl") is string;
            }
            set
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                    if (value) key.SetValue("PhoneCtrl", $"\"{ExePath}\"");
                    else key.DeleteValue("PhoneCtrl", false);
                    Log.Info($"Start with Windows: {value}");
                }
                catch (Exception ex)
                {
                    Log.Error("Could not change start-with-Windows", ex);
                    MessageBox.Show("Couldn't change the startup setting: " + ex.Message, "PhoneCtrl");
                }
            }
        }

        public void CreateDesktopShortcut()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var lnk = System.IO.Path.Combine(desktop, "PhoneCtrl.lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("Windows Script Host is not available.");
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(lnk);
                shortcut.TargetPath = ExePath;
                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(ExePath);
                shortcut.Description = "PhoneCtrl - control your iPhone from this PC";
                shortcut.Save();
                Log.Info("Desktop shortcut created: " + lnk);
                MessageBox.Show("A PhoneCtrl shortcut is now on your desktop.", "PhoneCtrl",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error("Could not create desktop shortcut", ex);
                MessageBox.Show("Couldn't create the shortcut: " + ex.Message, "PhoneCtrl");
            }
        }

        // ---- called from the tray menu ----

        public void ToggleWidget()
        {
            if (_widget == null) return;
            if (_widget.IsVisible) _widget.Hide(); else _widget.Show();
        }

        public bool WidgetVisible => _widget?.IsVisible == true;

        public void RestartBluetooth()
        {
            if (_phone != null) _ = _phone.StartAsync();
        }

        public void ReloadSettings()
        {
            Settings = AppSettings.Load();
            if (_phone != null) _phone.ReportIntervalMs = Settings.ReportIntervalMs;
            _capture?.SetExitShortcut(Settings.CaptureExitKey);
            _capture?.SetRightHoldToExit(Settings.CaptureExitRightHoldSeconds);
            _widget?.ApplySettings();
        }

        public static void OpenInShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Could not open " + path, ex);
            }
        }

        public void Quit()
        {
            Log.Info("PhoneCtrl exiting");
            _widget?.SavePosition();
            _capture?.Stop();
            _phone?.Stop();
            _tray?.Dispose();
            _widget?.Close();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
