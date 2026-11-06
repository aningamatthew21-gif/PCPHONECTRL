using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PhoneCtrl
{
    /// <summary>Icon in the Windows notification area (near the clock) with the app menu.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly App _app;

        public TrayIcon(App app)
        {
            _app = app;
            var menu = new ContextMenuStrip();
            var toggle = new ToolStripMenuItem("Hide widget");
            toggle.Click += (_, _) => _app.ToggleWidget();
            menu.Items.Add(toggle);
            menu.Items.Add("Use mouse on iPhone", null, (_, _) => _app.StartCapture());
            menu.Items.Add("Restart Bluetooth", null, (_, _) => _app.RestartBluetooth());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open settings file", null, (_, _) => App.OpenInShell(AppSettings.FilePath));
            menu.Items.Add("Reload settings", null, (_, _) => _app.ReloadSettings());
            menu.Items.Add("Open log folder", null, (_, _) => App.OpenInShell(Log.Folder));
            menu.Items.Add("Check for updates", null, async (_, _) => await _app.CheckForUpdatesAsync(userAsked: true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => _app.Quit());
            menu.Opening += (_, _) => toggle.Text = _app.WidgetVisible ? "Hide widget" : "Show widget";

            _icon = new NotifyIcon
            {
                Icon = DrawIcon(),
                Text = $"PhoneCtrl {Updater.CurrentVersion}",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) _app.ToggleWidget();
            };
        }

        /// <summary>Draws a small phone glyph so we don't need an .ico file.</summary>
        private static Icon DrawIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var body = new GraphicsPath();
                var r = new Rectangle(8, 2, 16, 28);
                int d = 6;
                body.AddArc(r.X, r.Y, d, d, 180, 90);
                body.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                body.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                body.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                body.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(255, 0, 120, 212));
                g.FillPath(fill, body);
                using var screen = new SolidBrush(Color.White);
                g.FillRectangle(screen, 11, 6, 10, 18);
                g.FillEllipse(screen, 14, 25, 4, 3);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public void Notify(string title, string text)
        {
            _icon.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
