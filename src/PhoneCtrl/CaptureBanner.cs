using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PhoneCtrl
{
    /// <summary>Small banner at the top of the screen while the mouse and keyboard are on the iPhone.</summary>
    internal sealed class CaptureBanner : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly TextBlock _text;

        public CaptureBanner()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            IsHitTestVisible = false;

            _text = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x00, 0x5A, 0xA6)),
                CornerRadius = new CornerRadius(0, 0, 14, 14),
                Padding = new Thickness(18, 10, 18, 10),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "",
                            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                            Foreground = Brushes.White,
                            FontSize = 16,
                            Margin = new Thickness(0, 0, 10, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        _text,
                    },
                },
            };
            SizeChanged += (_, _) => Position();
        }

        public void ShowFor(string exitKey)
        {
            _text.Text = $"Your mouse and keyboard are on the iPhone  -  press {exitKey} to come back";
            Show();
            Position();
        }

        private void Position()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            // no-activate | tool window | click-through
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x00000080 | 0x00000020);
        }
    }
}
