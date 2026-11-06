using System;
using System.Threading.Tasks;
using PhoneCtrl.Ble;

namespace PhoneCtrl
{
    public enum PhoneAction
    {
        PreviousTrack,
        PlayPause,
        NextTrack,
        VolumeDown,
        VolumeUp,
        PreviousVideo,
        NextVideo,
        Lock,
        CenterPointer,
        MouseToPhone,
    }

    /// <summary>Turns a widget button / hotkey into the right Bluetooth input for the iPhone.</summary>
    public sealed class PhoneActions
    {
        private readonly HidPeripheral _phone;
        private readonly Func<AppSettings> _settings;

        public PhoneActions(HidPeripheral phone, Func<AppSettings> settings)
        {
            _phone = phone;
            _settings = settings;
        }

        public Task RunAsync(PhoneAction action)
        {
            var s = _settings();
            Log.Info($"Action: {action}");
            int scrollSign = s.InvertScroll ? -1 : 1;
            switch (action)
            {
                case PhoneAction.PreviousTrack: return _phone.PressMediaKeyAsync(MediaKey.PreviousTrack);
                case PhoneAction.PlayPause: return _phone.PressMediaKeyAsync(MediaKey.PlayPause);
                case PhoneAction.NextTrack: return _phone.PressMediaKeyAsync(MediaKey.NextTrack);
                case PhoneAction.VolumeDown: return _phone.PressMediaKeyAsync(MediaKey.VolumeDown);
                case PhoneAction.VolumeUp: return _phone.PressMediaKeyAsync(MediaKey.VolumeUp);

                case PhoneAction.NextVideo: return FeedAsync(s, next: true, scrollSign);
                case PhoneAction.PreviousVideo: return FeedAsync(s, next: false, scrollSign);

                case PhoneAction.Lock:
                    return string.Equals(s.LockMethod, "PowerKey", StringComparison.OrdinalIgnoreCase)
                        ? _phone.PressMediaKeyAsync(MediaKey.Power)
                        : _phone.ClickAsync(s.LockMouseButton);

                case PhoneAction.CenterPointer: return CenterPointerAsync(s);
                default: return Task.CompletedTask;
            }
        }

        private Task FeedAsync(AppSettings s, bool next, int sign)
        {
            int direction = (next ? -1 : 1) * sign;
            if (string.Equals(s.ScrollMethod, "Wheel", StringComparison.OrdinalIgnoreCase))
                // Wheel "down" (negative) moves the feed to the next video, like a PC mouse wheel.
                return _phone.ScrollAsync(direction * s.ScrollNotches);
            // Swipe up (negative) = next video, exactly like flicking with a finger.
            return _phone.SwipeAsync(direction * s.SwipeDistance, s.SwipeSteps);
        }

        private async Task CenterPointerAsync(AppSettings s)
        {
            // Slam the pointer into the top-left corner, then move a known distance.
            await _phone.MoveAsync(-127 * 20, -127 * 40);
            await _phone.MoveAsync(s.CenterOffsetX, s.CenterOffsetY);
        }
    }
}
