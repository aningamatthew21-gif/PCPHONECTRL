using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneCtrl.Ble;

namespace PhoneCtrl
{
    /// <summary>
    /// The on-screen widget: a slim tab docked to a screen edge that opens into the control panel on hover.
    /// </summary>
    public partial class WidgetWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly App _app;
        private readonly HidPeripheral _phone;
        private readonly PhoneActions _actions;
        private readonly DispatcherTimer _collapseTimer;
        private readonly DispatcherTimer _messageTimer;
        private HotKeys? _hotKeys;

        private bool _rightEdge = true;
        private double _edgeX = double.NaN;
        private double _centerY = double.NaN;
        private bool _dragging;

        public WidgetWindow(App app, HidPeripheral phone, PhoneActions actions)
        {
            _app = app;
            _phone = phone;
            _actions = actions;
            InitializeComponent();

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); SetExpanded(false); };

            _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); ShowState(_phone.State, _phone.Detail); };

            _phone.StateChanged += (state, detail) => Dispatcher.InvokeAsync(() => ShowState(state, detail));
            SizeChanged += (_, _) => Place();
            ShowState(_phone.State, _phone.Detail);
            LoadPosition();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;

            // Don't steal focus from the app you're using, and stay out of Alt+Tab.
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            _hotKeys = new HotKeys(HwndSource.FromHwnd(hwnd), action => _ = RunAction(action));
            ApplySettings();
            Place();
        }

        public void ApplySettings()
        {
            if (_hotKeys == null) return;
            var s = _app.Settings;
            _hotKeys.Register(s.HotKeys);
            s.HotKeys.TryGetValue(nameof(PhoneAction.Lock), out var lockKey);
            HotkeyHint.Text = string.IsNullOrEmpty(lockKey) ? "" : $"Quick lock: {lockKey}";
            if (_hotKeys.Failures.Count > 0)
                HotkeyHint.Text += "\n⚠ " + string.Join("\n⚠ ", _hotKeys.Failures);
        }

        // ------------------------------------------------------------------ actions

        private void OnAction(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && Enum.TryParse(tag, out PhoneAction action))
                _ = RunAction(action);
        }

        private async System.Threading.Tasks.Task RunAction(PhoneAction action)
        {
            if (!_phone.IsConnected)
            {
                Flash("Connect your iPhone first");
                return;
            }
            await _actions.RunAsync(action);
        }

        private void Flash(string message)
        {
            StatusText.Text = message;
            StatusDot.Fill = MakeBrush("#E74C3C");
            if (ExpandedPanel.Visibility != Visibility.Visible)
            {
                SetExpanded(true);
                _collapseTimer.Stop();
                _collapseTimer.Interval = TimeSpan.FromSeconds(2.5);
                _collapseTimer.Start();
            }
            _messageTimer.Stop();
            _messageTimer.Start();
        }

        // ------------------------------------------------------------------ status

        private void ShowState(PeripheralState state, string detail)
        {
            string color = state switch
            {
                PeripheralState.Connected => "#2ECC71",
                PeripheralState.Advertising => "#F5A623",
                PeripheralState.Error => "#E74C3C",
                _ => "#8A8A8A",
            };
            StatusDot.Fill = MakeBrush(color);
            HandleDot.Fill = MakeBrush(color);
            StatusText.Text = string.IsNullOrEmpty(detail) ? state.ToString() : detail;
            StatusText.ToolTip = StatusText.Text + (state == PeripheralState.Error
                ? "\nRight-click the tray icon → Restart Bluetooth to try again."
                : "");
        }

        private static SolidColorBrush MakeBrush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        // ------------------------------------------------------------------ hover expand / collapse

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            _collapseTimer.Stop();
            SetExpanded(true);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (_dragging) return;
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(450);
            _collapseTimer.Start();
        }

        private void SetExpanded(bool expanded)
        {
            ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            Handle.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------ position

        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _dragging = true;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Mouse was released before the drag started.
            }
            finally
            {
                _dragging = false;
            }

            // Snap to whichever side of this monitor is closer.
            var wa = WorkAreaAt(Left + ActualWidth / 2, Top + ActualHeight / 2);
            _rightEdge = Left + ActualWidth / 2 > wa.Left + wa.Width / 2;
            _edgeX = _rightEdge ? wa.Right : wa.Left;
            _centerY = Top + ActualHeight / 2;
            Place();
            SavePosition();
        }

        private void LoadPosition()
        {
            var s = _app.Settings;
            _rightEdge = !string.Equals(s.Edge, "Left", StringComparison.OrdinalIgnoreCase);
            var primary = SystemParameters.WorkArea;
            _edgeX = s.EdgeX ?? (_rightEdge ? primary.Right : primary.Left);
            _centerY = s.CenterY ?? primary.Top + primary.Height / 2;

            // If the saved monitor is gone, fall back to the primary screen.
            var wa = WorkAreaAt(_rightEdge ? _edgeX - 1 : _edgeX + 1, _centerY);
            if (Math.Abs((_rightEdge ? wa.Right : wa.Left) - _edgeX) > 2)
            {
                _edgeX = _rightEdge ? primary.Right : primary.Left;
                _centerY = primary.Top + primary.Height / 2;
            }
            Left = _rightEdge ? _edgeX - 16 : _edgeX;
            Top = _centerY - 40;
        }

        public void SavePosition()
        {
            var s = _app.Settings;
            s.Edge = _rightEdge ? "Right" : "Left";
            s.EdgeX = _edgeX;
            s.CenterY = _centerY;
            s.Save();
        }

        /// <summary>Keep the widget glued to its edge and vertically centred on its saved point.</summary>
        private void Place()
        {
            if (double.IsNaN(_edgeX) || ActualWidth <= 0) return;
            var wa = WorkAreaAt(_rightEdge ? _edgeX - 1 : _edgeX + 1, _centerY);
            Left = _rightEdge ? _edgeX - ActualWidth : _edgeX;
            double top = _centerY - ActualHeight / 2;
            Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - ActualHeight));
        }

        /// <summary>Work area (screen minus taskbar) of the monitor at a point, in WPF units.</summary>
        private Rect WorkAreaAt(double x, double y)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point((int)(x * dpi.DpiScaleX), (int)(y * dpi.DpiScaleY)));
            var wa = screen.WorkingArea;
            return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY,
                wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
        }

        protected override void OnClosed(EventArgs e)
        {
            _hotKeys?.Dispose();
            base.OnClosed(e);
        }
    }
}
