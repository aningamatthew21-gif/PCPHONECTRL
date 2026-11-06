using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
            SizeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
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
                + $"\nTo come back: {_app.ExitHint()}.";

            HotkeyHint.Text = $"v{Updater.CurrentVersion}" + (string.IsNullOrEmpty(lockKey) ? "" : $"  ·  Quick lock: {lockKey}");
            HotkeyHint.ToolTip = null;
            if (_hotKeys.Failures.Count > 0)
            {
                // Usually temporary right after an update (the previous copy still holds them), so retry quietly.
                ScheduleHotKeyRetry();
                if (_hotKeyRetries >= MaxHotKeyRetries)
                {
                    HotkeyHint.Text += $"\n⚠ {_hotKeys.Failures.Count} shortcut(s) are taken by another app. Hover for details.";
                    HotkeyHint.ToolTip = string.Join("\n", _hotKeys.Failures)
                        + "\n\nChange them in the settings file (tray icon → Open settings file → HotKeys).";
                }
            }
            else
            {
                _hotKeyRetries = 0;
            }
        }

        private const int MaxHotKeyRetries = 8;
        private int _hotKeyRetries;
        private DispatcherTimer? _hotKeyRetryTimer;

        private void ScheduleHotKeyRetry()
        {
            if (_hotKeyRetries >= MaxHotKeyRetries || _hotKeyRetryTimer?.IsEnabled == true) return;
            _hotKeyRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hotKeyRetryTimer.Tick += (_, _) =>
            {
                _hotKeyRetryTimer!.Stop();
                _hotKeyRetries++;
                ApplySettings();
            };
            _hotKeyRetryTimer.Start();
        }

        /// <summary>Free the shortcuts so a newly started copy (after an update) can take them.</summary>
        public void ReleaseHotKeys()
        {
            _hotKeyRetryTimer?.Stop();
            _hotKeys?.Register(new System.Collections.Generic.Dictionary<string, string>());
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

        private enum UpdateUi { Idle, Checking, Available, Working, Message }
        private UpdateUi _updateUi = UpdateUi.Idle;
        private DispatcherTimer? _updateMessageTimer;

        public void ShowUpdateAvailable(Version version)
        {
            SetUpdateUi(UpdateUi.Available, $"Update to v{version}");
        }

        public void ShowUpdateProgress(string text)
        {
            SetUpdateUi(UpdateUi.Working, text);
        }

        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            switch (_updateUi)
            {
                case UpdateUi.Available:
                    _app.InstallUpdate();
                    return;
                case UpdateUi.Checking:
                case UpdateUi.Working:
                    return;
            }

            SetUpdateUi(UpdateUi.Checking, "Checking…");
            var started = DateTime.UtcNow;
            bool? result = await _app.CheckForUpdatesAsync(userAsked: true, fromWidget: true);

            // Let the spinner show for a moment so the click feels acknowledged.
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < TimeSpan.FromMilliseconds(700)) await Task.Delay(TimeSpan.FromMilliseconds(700) - elapsed);

            if (result == true) return; // App already switched the button to "Update to vX"
            SetUpdateUi(UpdateUi.Message, result == false
                ? $"You're up to date (v{Updater.CurrentVersion})"
                : "Couldn't check. Try again");
        }

        private void SetUpdateUi(UpdateUi state, string text)
        {
            _updateUi = state;
            UpdateText.Text = text;
            _updateMessageTimer?.Stop();

            bool blue = state == UpdateUi.Available || state == UpdateUi.Working;
            UpdateButton.Background = blue ? MakeBrush("#0078D4") : MakeBrush("#262626");
            UpdateButton.Foreground = blue ? Brushes.White : MakeBrush("#9A9A9A");
            UpdateIcon.Text = blue ? "\uE896" : "\uE895"; // download arrow / sync arrows
            UpdateBadge.Visibility = state == UpdateUi.Available ? Visibility.Visible : Visibility.Collapsed;
            UpdateButton.ToolTip = state switch
            {
                UpdateUi.Available => "Download and install the new version (the app restarts by itself)",
                UpdateUi.Idle => "Check GitHub for a newer version of PhoneCtrl",
                _ => null,
            };

            bool spin = state == UpdateUi.Checking || state == UpdateUi.Working;
            if (spin)
                UpdateIconRotation.BeginAnimation(RotateTransform.AngleProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
                    { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            else
            {
                UpdateIconRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                UpdateIconRotation.Angle = 0;
            }

            if (state == UpdateUi.Message)
            {
                _updateMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _updateMessageTimer.Tick += (_, _) =>
                {
                    _updateMessageTimer?.Stop();
                    if (_updateUi == UpdateUi.Message) SetUpdateUi(UpdateUi.Idle, "Check for updates");
                };
                _updateMessageTimer.Start();
            }
        }

        // ---- right-click menu ----

        private void OnMenuCheckUpdates(object sender, RoutedEventArgs e)
        {
            SetExpanded(true);
            OnUpdateClick(sender, e);
        }

        private void OnMenuHide(object sender, RoutedEventArgs e) => Hide();

        private void OnMenuExit(object sender, RoutedEventArgs e) => _app.Quit();

        // ------------------------------------------------------------------ now playing

        private string _artworkKey = "";
        private bool _waveformOn;

        public void ShowNowPlaying(NowPlaying np)
        {
            if (!_app.Settings.ShowNowPlaying || !np.HasTrack)
            {
                NowPlayingRow.Visibility = Visibility.Collapsed;
                SetWaveform(false);
                Handle.ToolTip = "PhoneCtrl - hover to open, drag to move";
                PlayPauseButton.Content = "\uE768";
                return;
            }

            NowPlayingRow.Visibility = Visibility.Visible;
            TrackTitle.Text = np.Title;
            TrackArtist.Text = string.IsNullOrWhiteSpace(np.Album) ? np.Artist : $"{np.Artist}  ·  {np.Album}";
            TrackApp.Text = (np.IsPlaying ? "Playing" : "Paused") + (string.IsNullOrWhiteSpace(np.PlayerName) ? "" : $" in {np.PlayerName}");
            PlayPauseButton.Content = np.IsPlaying ? "\uE769" : "\uE768"; // pause / play icon
            Handle.ToolTip = $"{(np.IsPlaying ? "♪ " : "")}{np.Title} - {np.Artist}";
            SetWaveform(np.IsPlaying);

            string key = $"{np.Artist}\n{np.Title}";
            if (key != _artworkKey)
            {
                _artworkKey = key;
                ArtworkView.Background = null;
                if (_app.Settings.LookUpAlbumArt) _ = LoadArtworkAsync(np, key);
            }
        }

        private async Task LoadArtworkAsync(NowPlaying np, string key)
        {
            var bytes = await ArtworkLookup.FindAsync(np.Artist, np.Title, np.Album);
            if (bytes == null || key != _artworkKey) return;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new System.IO.MemoryStream(bytes);
                image.DecodePixelWidth = 92;
                image.EndInit();
                image.Freeze();
                ArtworkView.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex)
            {
                Log.Info("Could not show artwork: " + ex.Message);
            }
        }

        private DispatcherTimer? _waveTimer;
        private readonly Random _random = new Random();
        private readonly double[] _barLevels = { 0.4, 0.6, 0.5, 0.3 };

        /// <summary>
        /// Bars on the pill while something plays on the iPhone. Bluetooth control doesn't carry the audio itself,
        /// so this can't follow the real beat. Instead each bar wanders randomly, rising fast and falling slower,
        /// so it reads like a live level meter rather than a fixed loop.
        /// </summary>
        private void SetWaveform(bool playing)
        {
            if (playing == _waveformOn) return;
            _waveformOn = playing;
            Waveform.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;

            if (playing)
            {
                _waveTimer ??= CreateWaveTimer();
                _waveTimer.Start();
            }
            else
            {
                _waveTimer?.Stop();
                foreach (var child in Waveform.Children)
                    if (child is Rectangle bar && bar.RenderTransform is ScaleTransform scale)
                    {
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        scale.ScaleY = 0.4;
                    }
            }
        }

        private DispatcherTimer CreateWaveTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(130) };
            timer.Tick += (_, _) =>
            {
                int i = 0;
                foreach (var child in Waveform.Children)
                {
                    if (child is not Rectangle bar || bar.RenderTransform is not ScaleTransform scale) { i++; continue; }
                    double previous = _barLevels[i];
                    // Mostly small moves with occasional jumps, like music levels.
                    double target = _random.NextDouble() < 0.25
                        ? 0.25 + _random.NextDouble() * 0.75
                        : Math.Clamp(previous + (_random.NextDouble() - 0.5) * 0.5, 0.2, 1.0);
                    _barLevels[i] = target;
                    bool rising = target > previous;
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target,
                        TimeSpan.FromMilliseconds(rising ? 90 : 220))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = rising ? EasingMode.EaseOut : EasingMode.EaseIn },
                    }, HandoffBehavior.SnapshotAndReplace);
                    i++;
                }
            };
            return timer;
        }

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
                detail = "Mouse on iPhone (hold right button to return)";
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
                HandleContent.Orientation = Orientation.Horizontal;
                Waveform.Margin = new Thickness(7, 0, 0, 0);
                Handle.Width = 80;
                Handle.Height = 16;
                Sections.Orientation = Orientation.Horizontal;
                MusicSection.Width = 200;
                VolumeSection.Width = 104;
                VideoSection.Width = 104;
                LockSection.Width = 140;
                foreach (FrameworkElement s in Sections.Children) s.Margin = new Thickness(0, 0, 6, 0);
            }
            else
            {
                HandleContent.Orientation = Orientation.Vertical;
                Waveform.Margin = new Thickness(0, 7, 0, 0);
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
        //
        // All positioning is done in physical screen pixels with Win32 calls. WPF's own Left/Top are in
        // DPI-scaled units, which change meaning when the window crosses onto a monitor with a different
        // scale. Mixing the two caused a resize → re-snap → resize loop that froze the app.

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        private bool _placing;

        private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

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

            if (!GetWindowRect(Hwnd, out var r)) return;
            var center = new System.Drawing.Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            var wa = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;

            // Snap to whichever edge of the monitor it was dropped on is closest.
            int dLeft = center.X - wa.Left, dRight = wa.Right - center.X;
            int dTop = center.Y - wa.Top, dBottom = wa.Bottom - center.Y;
            int min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
            _edge = min == dLeft ? DockEdge.Left
                : min == dRight ? DockEdge.Right
                : min == dTop ? DockEdge.Top
                : DockEdge.Bottom;
            _anchor = new Point(center.X, center.Y);
            ApplyOrientation();
            Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
            SavePosition();
        }

        private void LoadPosition()
        {
            var s = _app.Settings;
            if (!Enum.TryParse(s.Edge, true, out _edge)) _edge = DockEdge.Right;
            var primary = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            _anchor = s.AnchorX.HasValue && s.AnchorY.HasValue
                ? new Point(s.AnchorX.Value, s.AnchorY.Value)
                : new Point(primary.Left + primary.Width / 2, primary.Top + primary.Height / 2);
            ApplyOrientation();
        }

        public void SavePosition()
        {
            var s = _app.Settings;
            s.Edge = _edge.ToString();
            s.AnchorX = _anchor.X;
            s.AnchorY = _anchor.Y;
            s.Save();
        }

        /// <summary>Keep the widget glued to its edge (in screen pixels), centred on its saved spot along that edge.</summary>
        private void Place()
        {
            if (_dragging || _placing || double.IsNaN(_anchor.X)) return;
            var hwnd = Hwnd;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;

            _placing = true;
            try
            {
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return;
                var wa = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)_anchor.X, (int)_anchor.Y)).WorkingArea;

                int ax = (int)_anchor.X, ay = (int)_anchor.Y;
                int x = Math.Max(wa.Left, Math.Min(ax - w / 2, wa.Right - w));
                int y = Math.Max(wa.Top, Math.Min(ay - h / 2, wa.Bottom - h));
                switch (_edge)
                {
                    case DockEdge.Left: x = wa.Left; break;
                    case DockEdge.Right: x = wa.Right - w; break;
                    case DockEdge.Top: y = wa.Top; break;
                    case DockEdge.Bottom: y = wa.Bottom - h; break;
                }

                if (x != r.Left || y != r.Top)
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            finally
            {
                _placing = false;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _hotKeys?.Dispose();
            base.OnClosed(e);
        }
    }
}
