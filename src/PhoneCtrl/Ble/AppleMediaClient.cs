using System;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PhoneCtrl.Ble
{
    /// <summary>What the iPhone is playing right now.</summary>
    public sealed class NowPlaying
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        /// <summary>The app playing it, e.g. "Spotify" or "Music".</summary>
        public string PlayerName { get; set; } = "";
        public bool IsPlaying { get; set; }
        public double DurationSeconds { get; set; }
        public double ElapsedSeconds { get; set; }

        public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

        public NowPlaying Clone() => (NowPlaying)MemberwiseClone();
    }

    /// <summary>
    /// Reads "now playing" from the iPhone with Apple Media Service (AMS), a Bluetooth LE service built into iOS.
    /// The PC acts as a GATT client on the connection the iPhone already opened for the keyboard/mouse.
    /// AMS gives track title, artist, album, duration and play state. It does not provide artwork.
    /// Spec: Apple "Apple Media Service Reference".
    /// </summary>
    public sealed class AppleMediaClient
    {
        private static readonly Guid AmsService = new Guid("89D3502B-0F36-433A-8EF4-C502AD55F8DC");
        private static readonly Guid EntityUpdateUuid = new Guid("2F7CABCE-808D-411F-9A0C-BB92BA96C102");

        private const byte EntityPlayer = 0, EntityTrack = 2;
        private const byte PlayerName = 0, PlayerPlaybackInfo = 1;
        private const byte TrackArtist = 0, TrackAlbum = 1, TrackTitle = 2, TrackDuration = 3;

        private readonly NowPlaying _current = new NowPlaying();
        private BluetoothLEDevice? _device;
        private GattDeviceService? _service;
        private GattCharacteristic? _entityUpdate;
        private string? _deviceId;
        private bool _connecting;

        /// <summary>Raised (on a background thread) whenever anything about the current track changes.</summary>
        public event Action<NowPlaying>? Changed;

        public bool IsConnected => _entityUpdate != null;

        public async Task ConnectAsync(string deviceId)
        {
            if (_connecting || (IsConnected && deviceId == _deviceId)) return;
            _connecting = true;
            Disconnect();
            _deviceId = deviceId;
            try
            {
                _device = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (_device == null)
                {
                    Log.Info("AMS: phone not found");
                    return;
                }

                GattDeviceServicesResult services = null!;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    services = await _device.GetGattServicesForUuidAsync(AmsService, BluetoothCacheMode.Uncached);
                    if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0) break;
                    await Task.Delay(1500);
                }
                if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                {
                    Log.Info($"AMS: not available on this phone ({services.Status})");
                    return;
                }
                _service = services.Services[0];

                var chars = await _service.GetCharacteristicsForUuidAsync(EntityUpdateUuid, BluetoothCacheMode.Uncached);
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                {
                    Log.Info($"AMS: entity update characteristic unavailable ({chars.Status})");
                    return;
                }
                var entityUpdate = chars.Characteristics[0];
                entityUpdate.ValueChanged += OnEntityUpdate;

                var cccd = await entityUpdate.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (cccd != GattCommunicationStatus.Success)
                {
                    Log.Info($"AMS: subscribe failed ({cccd})");
                    entityUpdate.ValueChanged -= OnEntityUpdate;
                    return;
                }

                // Register for the attributes we want. iOS then notifies us whenever they change.
                await RegisterAsync(entityUpdate, EntityPlayer, PlayerName, PlayerPlaybackInfo);
                await RegisterAsync(entityUpdate, EntityTrack, TrackArtist, TrackAlbum, TrackTitle, TrackDuration);
                _entityUpdate = entityUpdate;
                Log.Info("AMS: connected, receiving now-playing info");
            }
            catch (Exception ex)
            {
                Log.Error("AMS connect failed", ex);
            }
            finally
            {
                _connecting = false;
            }
        }

        private static async Task RegisterAsync(GattCharacteristic c, byte entity, params byte[] attributes)
        {
            var payload = new byte[attributes.Length + 1];
            payload[0] = entity;
            Array.Copy(attributes, 0, payload, 1, attributes.Length);
            var result = await c.WriteValueWithResultAsync(payload.AsBuffer(), GattWriteOption.WriteWithResponse);
            if (result.Status != GattCommunicationStatus.Success)
                Log.Info($"AMS: register entity {entity} failed ({result.Status}, protocol error {result.ProtocolError})");
        }

        public void Disconnect()
        {
            try
            {
                if (_entityUpdate != null) _entityUpdate.ValueChanged -= OnEntityUpdate;
                _service?.Dispose();
                _device?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("AMS disconnect failed", ex);
            }
            _entityUpdate = null;
            _service = null;
            _device = null;

            bool hadTrack;
            lock (_current)
            {
                hadTrack = _current.HasTrack || _current.IsPlaying;
                _current.Title = _current.Artist = _current.Album = _current.PlayerName = "";
                _current.IsPlaying = false;
            }
            if (hadTrack) Changed?.Invoke(_current.Clone());
        }

        /// <summary>Notification format: [EntityID, AttributeID, Flags, UTF-8 value…]</summary>
        private void OnEntityUpdate(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                var data = args.CharacteristicValue.ToArray();
                if (data.Length < 3) return;
                byte entity = data[0], attribute = data[1];
                string value = Encoding.UTF8.GetString(data, 3, data.Length - 3);

                NowPlaying snapshot;
                lock (_current)
                {
                    if (entity == EntityPlayer && attribute == PlayerName)
                        _current.PlayerName = value;
                    else if (entity == EntityPlayer && attribute == PlayerPlaybackInfo)
                    {
                        // "PlaybackState,PlaybackRate,ElapsedTime": state 0 paused, 1 playing, 2 rewinding, 3 fast-forwarding
                        var parts = value.Split(',');
                        _current.IsPlaying = parts.Length > 0 && parts[0] != "0" && parts[0] != "";
                        if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed))
                            _current.ElapsedSeconds = elapsed;
                    }
                    else if (entity == EntityTrack && attribute == TrackArtist) _current.Artist = value;
                    else if (entity == EntityTrack && attribute == TrackAlbum) _current.Album = value;
                    else if (entity == EntityTrack && attribute == TrackTitle) _current.Title = value;
                    else if (entity == EntityTrack && attribute == TrackDuration
                        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                        _current.DurationSeconds = duration;
                    else return;
                    snapshot = _current.Clone();
                }
                Changed?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                Log.Error("AMS update parse failed", ex);
            }
        }
    }
}
