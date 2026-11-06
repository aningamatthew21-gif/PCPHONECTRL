using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneCtrl.Ble;

namespace PhoneCtrl
{
    public enum DockEdge { Left, Right, Top, Bottom }

    /// <summary>
    /// The on-screen widget: a slim tab docked to any screen edge that opens into the control panel on hover.
    /// On the left/right edges it is tall (portrait); on the top/bottom edges it lays out sideways (landscape).
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

        private DockEdge _edge = DockEdge.Right;
        private Point _anchor = new Point(double.NaN, double.NaN); // a point on our monitor; also our spot along the edge
        private bool _dragging;
        private bool _captureActive;
        private DateTime _lastWheelSwipe = DateTime.MinValue;

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
            s.HotKeys.TryGetValue(nameof(PhoneAction.MouseToPhone), out var mouseKey);
            MouseToPhoneButton.ToolTip = "Use your mouse and keyboard on the iPhone"
                + (string.IsNullOrEmpty(mouseKey) ? "" : $" ({mouseKey})")
                + $"\nPress {s.CaptureExitKey} to bring them back to the PC.";

            HotkeyHint.Text = $"v{Updater.CurrentVersion}" + (string.IsNullOrEmpty(lockKey) ? "" : $"  ·  Quick lock: {lockKey}");
            if (_hotKeys.Failures.Count > 0)
                HotkeyHint.Text += "\n⚠ " + string.Join("\n⚠ ", _hotKeys.Failures);
        }

        // ------------------------------------------------------------------ actions

        private void OnAction(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && Enum.TryParse(tag, out PhoneAction action))
                _ = RunAction(action);
        }

        private async Task RunAction(PhoneAction action)
        {
            if (!_phone.IsConnected)
            {
                Flash("Connect your iPhone first");
                return;
            }
            if (action == PhoneAction.MouseToPhone)
            {
                SetExpanded(false);
                if (!_app.StartCapture()) Flash("Couldn't take over the mouse");
                return;
            }
            await _actions.RunAsync(action);
        }

        /// <summary>Mouse wheel over the video pad: wheel down = next video, wheel up = previous.</summary>
        private void OnVideoPadWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            // One swipe per wheel "flick", so a fast scroll doesn't skip five videos.
            if (DateTime.UtcNow - _lastWheelSwipe < TimeSpan.FromMilliseconds(450)) return;
            _lastWheelSwipe = DateTime.UtcNow;

            bool next = e.Delta < 0;
            PulseArrow(next ? NextVideoButton : PrevVideoButton);
            _ = RunAction(next ? PhoneAction.NextVideo : PhoneAction.PreviousVideo);
        }

        private void PulseArrow(Button arrow)
        {
            arrow.Foreground = Brushes.White;
            arrow.Background = MakeBrush("#0078D4");
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                arrow.ClearValue(ForegroundProperty);
                arrow.ClearValue(BackgroundProperty);
            };
            t.Start();
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

        // ------------------------------------------------------------------ updates

        public void ShowUpdateAvailable(Version version)
        {
            UpdateText.Text = $"Update to v{version}";
            UpdateButton.IsEnabled = true;
            UpdateButton.Visibility = Visibility.Visible;
            UpdateBadge.Visibility = Visibility.Visible;
        }

        public void ShowUpdateProgress(string text)
        {
            UpdateText.Text = text;
            UpdateButton.IsEnabled = false;
            UpdateButton.Visibility = Visibility.Visible;
        }

        private void OnUpdateClick(object sender, RoutedEventArgs e) => _app.InstallUpdate();

        // ------------------------------------------------------------------ status

        public void SetCaptureActive(bool active)
        {
            _captureActive = active;
            ShowState(_phone.State, _phone.Detail);
        }

        private void ShowState(PeripheralState state, string detail)
        {
            string color = state switch
            {
                PeripheralState.Connected => "#2ECC71",
                PeripheralState.Advertising => "#F5A623",
                PeripheralState.Error => "#E74C3C",
                _ => "#8A8A8A",
            };
            if (_captureActive && state == PeripheralState.Connected)
            {
                color = "#4CC2FF";
                detail = $"Mouse on iPhone ({_app.Settings.CaptureExitKey})";
            }
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

        // ------------------------------------------------------------------ layout (portrait / landscape)

        private bool Horizontal => _edge == DockEdge.Top || _edge == DockEdge.Bottom;

        private void ApplyOrientation()
        {
            if (Horizontal)
            {
                Handle.Width = 80;
                Handle.Height = 16;
                Sections.Orientation = Orientation.Horizontal;
                MusicSection.Width = 150;
                VolumeSection.Width = 104;
                VideoSection.Width = 104;
                LockSection.Width = 140;
                foreach (FrameworkElement s in Sections.Children) s.Margin = new Thickness(0, 0, 6, 0);
            }
            else
            {
                Handle.Width = 16;
                Handle.Height = 80;
                Sections.Orientation = Orientation.Vertical;
                foreach (FrameworkElement s in Sections.Children)
                {
                    s.Width = 168;
                    s.Margin = new Thickness(0);
                }
            }
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

            // Snap to whichever edge of this monitor is closest.
            var center = new Point(Left + ActualWidth / 2, Top + ActualHeight / 2);
            var wa = WorkAreaAt(center);
            double dLeft = center.X - wa.Left, dRight = wa.Right - center.X;
            double dTop = center.Y - wa.Top, dBottom = wa.Bottom - center.Y;
            double min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
            _edge = min == dLeft ? DockEdge.Left
                : min == dRight ? DockEdge.Right
                : min == dTop ? DockEdge.Top
                : DockEdge.Bottom;
            _anchor = center;
            ApplyOrientation();
            UpdateLayout();
            Place();
            SavePosition();
        }

        private void LoadPosition()
        {
            var s = _app.Settings;
            if (!Enum.TryParse(s.Edge, true, out _edge)) _edge = DockEdge.Right;
            var primary = SystemParameters.WorkArea;
            _anchor = s.AnchorX.HasValue && s.AnchorY.HasValue
                ? new Point(s.AnchorX.Value, s.AnchorY.Value)
                : new Point(primary.Left + primary.Width / 2, primary.Top + primary.Height / 2);
            ApplyOrientation();
            Left = _anchor.X;
            Top = _anchor.Y;
        }

        public void SavePosition()
        {
            var s = _app.Settings;
            s.Edge = _edge.ToString();
            s.AnchorX = _anchor.X;
            s.AnchorY = _anchor.Y;
            s.Save();
        }

        /// <summary>Keep the widget glued to its edge, centred on its saved spot along that edge.</summary>
        private void Place()
        {
            if (double.IsNaN(_anchor.X) || ActualWidth <= 0) return;
            var wa = WorkAreaAt(_anchor);
            double w = ActualWidth, h = ActualHeight;
            double x = Math.Max(wa.Left, Math.Min(_anchor.X - w / 2, wa.Right - w));
            double y = Math.Max(wa.Top, Math.Min(_anchor.Y - h / 2, wa.Bottom - h));
            switch (_edge)
            {
                case DockEdge.Left: Left = wa.Left; Top = y; break;
                case DockEdge.Right: Left = wa.Right - w; Top = y; break;
                case DockEdge.Top: Top = wa.Top; Left = x; break;
                case DockEdge.Bottom: Top = wa.Bottom - h; Left = x; break;
            }
        }

        /// <summary>Work area (screen minus taskbar) of the monitor nearest a point, in WPF units.</summary>
        private Rect WorkAreaAt(Point p)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point((int)(p.X * dpi.DpiScaleX), (int)(p.Y * dpi.DpiScaleY)));
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
