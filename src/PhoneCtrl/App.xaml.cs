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

        public AppSettings Settings { get; private set; } = new AppSettings();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

            Log.Info("PhoneCtrl starting");
            Settings = AppSettings.Load();

            _phone = new HidPeripheral { ReportIntervalMs = Settings.ReportIntervalMs };
            var actions = new PhoneActions(_phone, () => Settings);

            _capture = new InputCapture(_phone, Dispatcher);
            _capture.SetExitShortcut(Settings.CaptureExitKey);
            _banner = new CaptureBanner();
            _capture.ActiveChanged += active =>
            {
                if (active) _banner.ShowFor(Settings.CaptureExitKey); else _banner.Hide();
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

        /// <summary>Send this PC's mouse and keyboard to the iPhone until the exit shortcut is pressed.</summary>
        public bool StartCapture() => _capture?.Start() == true;

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
