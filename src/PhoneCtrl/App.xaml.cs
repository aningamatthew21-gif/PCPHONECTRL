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

            _widget = new WidgetWindow(this, _phone, actions);
            _widget.Show();

            _tray = new TrayIcon(this);

            _ = _phone.StartAsync();
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
