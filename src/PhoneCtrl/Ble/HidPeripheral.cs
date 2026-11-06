using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

namespace PhoneCtrl.Ble
{
    public enum PeripheralState { Stopped, Starting, Advertising, Connected, Error }

    /// <summary>
    /// Publishes a Bluetooth LE "HID over GATT" service using the PC's built-in radio,
    /// so the iPhone sees this PC as a keyboard + mouse + media remote.
    ///
    /// The connection is one-way: we only ever send input reports to the phone.
    /// Nothing is read from the phone.
    /// </summary>
    public sealed class HidPeripheral
    {
        private static Guid U16(ushort v) => new Guid($"0000{v:X4}-0000-1000-8000-00805f9b34fb");

        private static readonly Guid HidServiceUuid = U16(0x1812);
        private static readonly Guid BatteryServiceUuid = U16(0x180F);
        private static readonly Guid HidInformationUuid = U16(0x2A4A);
        private static readonly Guid ReportMapUuid = U16(0x2A4B);
        private static readonly Guid HidControlPointUuid = U16(0x2A4C);
        private static readonly Guid ReportUuid = U16(0x2A4D);
        private static readonly Guid ProtocolModeUuid = U16(0x2A4E);
        private static readonly Guid BootMouseInputUuid = U16(0x2A33);
        private static readonly Guid BatteryLevelUuid = U16(0x2A19);
        private static readonly Guid ReportReferenceUuid = U16(0x2908);

        private const byte ReportTypeInput = 1;
        private const byte ReportTypeOutput = 2;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly List<GattLocalCharacteristic> _inputs = new List<GattLocalCharacteristic>();

        private GattServiceProvider? _hid;
        private GattServiceProvider? _battery;
        private GattLocalCharacteristic? _keyboardIn;
        private GattLocalCharacteristic? _mouseIn;
        private GattLocalCharacteristic? _consumerIn;
        private DateTime _lastSend = DateTime.MinValue;

        /// <summary>Minimum gap between two reports. iOS drops reports sent faster than the link interval.</summary>
        public int ReportIntervalMs { get; set; } = 15;

        public PeripheralState State { get; private set; } = PeripheralState.Stopped;
        public string Detail { get; private set; } = "";
        public bool IsConnected => State == PeripheralState.Connected;

        /// <summary>Bluetooth name of the connected phone, e.g. "Matthew's iPhone".</summary>
        public string? ConnectedDeviceName { get; private set; }

        /// <summary>Raised on a background thread whenever <see cref="State"/> changes.</summary>
        public event Action<PeripheralState, string>? StateChanged;

        // ------------------------------------------------------------------ start / stop

        public async Task StartAsync()
        {
            Stop();
            SetState(PeripheralState.Starting, "Starting Bluetooth…");
            try
            {
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                if (adapter == null)
                {
                    SetState(PeripheralState.Error, "No Bluetooth adapter found.");
                    return;
                }
                Log.Info($"Adapter LE={adapter.IsLowEnergySupported} Peripheral={adapter.IsPeripheralRoleSupported}");
                if (!adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
                {
                    SetState(PeripheralState.Error, "This PC's Bluetooth can't act as a keyboard (no LE peripheral role).");
                    return;
                }
                var radio = await adapter.GetRadioAsync();
                if (radio != null && radio.State != RadioState.On)
                {
                    SetState(PeripheralState.Error, "Bluetooth is turned off. Turn it on in Windows Settings.");
                    return;
                }

                await BuildHidServiceAsync();
                await BuildBatteryServiceAsync();

                var adv = new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                };
                _hid!.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
                _hid.StartAdvertising(adv);
                _battery!.StartAdvertising(adv);
                Log.Info("Advertising started");
                UpdateConnectionState();
            }
            catch (Exception ex)
            {
                Log.Error("Start failed", ex);
                SetState(PeripheralState.Error, "Bluetooth start failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            try
            {
                if (_hid != null)
                {
                    _hid.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
                    _hid.StopAdvertising();
                }
                _battery?.StopAdvertising();
            }
            catch (Exception ex)
            {
                Log.Error("Stop failed", ex);
            }
            foreach (var c in _inputs) c.SubscribedClientsChanged -= OnSubscribersChanged;
            _inputs.Clear();
            _hid = null;
            _battery = null;
            _keyboardIn = _mouseIn = _consumerIn = null;
            ConnectedDeviceName = null;
            SetState(PeripheralState.Stopped, "Stopped");
        }

        // ------------------------------------------------------------------ public actions

        /// <summary>Press and release a media key (next song, volume, …).</summary>
        public Task PressMediaKeyAsync(MediaKey key) => RunAsync(async () =>
        {
            await NotifyAsync(_consumerIn, new[] { (byte)key });
            await NotifyAsync(_consumerIn, new byte[] { 0 });
        });

        /// <summary>Scroll the wheel. Positive = up, negative = down.</summary>
        public Task ScrollAsync(int notches) => RunAsync(async () =>
        {
            int remaining = notches;
            while (remaining != 0)
            {
                int step = Math.Clamp(remaining, -127, 127);
                await NotifyAsync(_mouseIn, MouseReport(0, 0, 0, step));
                remaining -= step;
            }
        });

        /// <summary>Click a mouse button (1 = left, 2 = right, 3 = middle, 4, 5).</summary>
        public Task ClickAsync(int button, int holdMs = 60) => RunAsync(async () =>
        {
            byte mask = (byte)(1 << (Math.Clamp(button, 1, 5) - 1));
            await NotifyAsync(_mouseIn, MouseReport(mask, 0, 0, 0));
            await Task.Delay(holdMs);
            await NotifyAsync(_mouseIn, MouseReport(0, 0, 0, 0));
        });

        /// <summary>Move the pointer by a relative amount (split into ±127 steps).</summary>
        public Task MoveAsync(int dx, int dy) => RunAsync(async () =>
        {
            while (dx != 0 || dy != 0)
            {
                int sx = Math.Clamp(dx, -127, 127);
                int sy = Math.Clamp(dy, -127, 127);
                await NotifyAsync(_mouseIn, MouseReport(0, sx, sy, 0));
                dx -= sx;
                dy -= sy;
            }
        });

        /// <summary>Press and release a keyboard key (HID usage code) with optional modifier bits.</summary>
        public Task PressKeyAsync(byte usage, byte modifiers = 0) => RunAsync(async () =>
        {
            await NotifyAsync(_keyboardIn, new byte[] { modifiers, 0, usage, 0, 0, 0, 0, 0 });
            await NotifyAsync(_keyboardIn, new byte[8]);
        });

        /// <summary>Send one raw mouse report (used by "mouse on iPhone" mode).</summary>
        public Task SendMouseAsync(byte buttons, int dx, int dy, int wheel) => RunAsync(async () =>
        {
            // Large movements are split so every report stays within the ±127 range.
            do
            {
                int sx = Math.Clamp(dx, -127, 127);
                int sy = Math.Clamp(dy, -127, 127);
                int sw = Math.Clamp(wheel, -127, 127);
                await NotifyAsync(_mouseIn, MouseReport(buttons, sx, sy, sw));
                dx -= sx;
                dy -= sy;
                wheel -= sw;
            } while (dx != 0 || dy != 0 || wheel != 0);
        });

        /// <summary>Send one raw keyboard report: modifier bits + up to 6 pressed key usages.</summary>
        public Task SendKeyboardAsync(byte modifiers, IReadOnlyList<byte> keys) => RunAsync(async () =>
        {
            var report = new byte[8];
            report[0] = modifiers;
            for (int i = 0; i < keys.Count && i < 6; i++) report[2 + i] = keys[i];
            await NotifyAsync(_keyboardIn, report);
        });

        /// <summary>
        /// Finger-style swipe with the AssistiveTouch pointer: press, drag vertically, release,
        /// then move the pointer back to where it started. Negative = swipe up (next video).
        /// </summary>
        public Task SwipeAsync(int totalDy, int steps) => RunAsync(async () =>
        {
            steps = Math.Max(2, steps);
            int per = Math.Clamp(totalDy / steps, -127, 127);
            if (per == 0) per = totalDy < 0 ? -1 : 1;

            await NotifyAsync(_mouseIn, MouseReport(0, 1, 0, 0));   // wake the pointer
            await NotifyAsync(_mouseIn, MouseReport(0, -1, 0, 0));
            await NotifyAsync(_mouseIn, MouseReport(1, 0, 0, 0));   // finger down
            for (int i = 0; i < steps; i++)
                await NotifyAsync(_mouseIn, MouseReport(1, 0, per, 0));
            await NotifyAsync(_mouseIn, MouseReport(0, 0, 0, 0));   // finger up
            for (int i = 0; i < steps; i++)
                await NotifyAsync(_mouseIn, MouseReport(0, 0, -per, 0)); // pointer back
        });

        // ------------------------------------------------------------------ GATT construction

        private async Task BuildHidServiceAsync()
        {
            var result = await GattServiceProvider.CreateAsync(HidServiceUuid);
            if (result.Error != BluetoothError.Success)
                throw new InvalidOperationException($"Windows refused the HID service ({result.Error}).");
            _hid = result.ServiceProvider;
            var svc = _hid.Service;

            // HID Information: bcdHID 1.11, country 0, flags = normally connectable
            await AddCharacteristicAsync(svc, HidInformationUuid, GattCharacteristicProperties.Read,
                new byte[] { 0x11, 0x01, 0x00, 0x02 });

            await AddCharacteristicAsync(svc, ReportMapUuid, GattCharacteristicProperties.Read, HidReports.ReportMap);

            var controlPoint = await AddCharacteristicAsync(svc, HidControlPointUuid,
                GattCharacteristicProperties.WriteWithoutResponse, null);
            controlPoint.WriteRequested += AcceptWrite;

            var protocolMode = await AddCharacteristicAsync(svc, ProtocolModeUuid,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
                new byte[] { 0x01 }); // report protocol
            protocolMode.WriteRequested += AcceptWrite;

            _keyboardIn = await AddReportAsync(svc, HidReports.KeyboardId, ReportTypeInput, 8);

            var keyboardOut = await AddReportAsync(svc, HidReports.KeyboardId, ReportTypeOutput, 1);
            keyboardOut.WriteRequested += AcceptWrite; // Caps Lock LED etc. - ignored

            _mouseIn = await AddReportAsync(svc, HidReports.MouseId, ReportTypeInput, 4);
            _consumerIn = await AddReportAsync(svc, HidReports.ConsumerId, ReportTypeInput, 1);

            var bootMouse = await AddCharacteristicAsync(svc, BootMouseInputUuid,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, new byte[3]);

            foreach (var c in new[] { _keyboardIn, _mouseIn, _consumerIn, bootMouse })
            {
                _inputs.Add(c);
                c.SubscribedClientsChanged += OnSubscribersChanged;
            }
            Log.Info("HID service built");
        }

        private async Task BuildBatteryServiceAsync()
        {
            var result = await GattServiceProvider.CreateAsync(BatteryServiceUuid);
            if (result.Error != BluetoothError.Success)
                throw new InvalidOperationException($"Windows refused the battery service ({result.Error}).");
            _battery = result.ServiceProvider;
            await AddCharacteristicAsync(_battery.Service, BatteryLevelUuid,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, new byte[] { 100 });
        }

        private static async Task<GattLocalCharacteristic> AddCharacteristicAsync(
            GattLocalService svc, Guid uuid, GattCharacteristicProperties props, byte[]? staticValue)
        {
            var p = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = props,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
            };
            if (staticValue != null) p.StaticValue = staticValue.AsBuffer();

            var r = await svc.CreateCharacteristicAsync(uuid, p);
            if (r.Error != BluetoothError.Success)
                throw new InvalidOperationException($"Could not create characteristic {uuid} ({r.Error}).");
            return r.Characteristic;
        }

        private static async Task<GattLocalCharacteristic> AddReportAsync(
            GattLocalService svc, byte reportId, byte reportType, int length)
        {
            var props = reportType == ReportTypeInput
                ? GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify
                : GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse;

            var c = await AddCharacteristicAsync(svc, ReportUuid, props, new byte[length]);

            var d = await c.CreateDescriptorAsync(ReportReferenceUuid, new GattLocalDescriptorParameters
            {
                StaticValue = new[] { reportId, reportType }.AsBuffer(),
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            });
            if (d.Error != BluetoothError.Success)
                throw new InvalidOperationException($"Could not create report reference ({d.Error}).");
            return c;
        }

        private static async void AcceptWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request != null && request.Option == GattWriteOption.WriteWithResponse)
                    request.Respond();
            }
            catch (Exception ex)
            {
                Log.Error("Write handling failed", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        // ------------------------------------------------------------------ sending

        private static byte[] MouseReport(byte buttons, int x, int y, int wheel) =>
            new[] { buttons, unchecked((byte)(sbyte)x), unchecked((byte)(sbyte)y), unchecked((byte)(sbyte)wheel) };

        private async Task RunAsync(Func<Task> action)
        {
            if (!IsConnected)
            {
                Log.Info("Ignored action: iPhone not connected");
                return;
            }
            await _sendLock.WaitAsync();
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Error("Send failed", ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task NotifyAsync(GattLocalCharacteristic? c, byte[] data)
        {
            if (c == null || c.SubscribedClients.Count == 0) return;

            var wait = _lastSend.AddMilliseconds(ReportIntervalMs) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            await c.NotifyValueAsync(data.AsBuffer());
            _lastSend = DateTime.UtcNow;
        }

        // ------------------------------------------------------------------ state

        private void OnAdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
        {
            Log.Info($"Advertisement status: {args.Status} ({args.Error})");
            if (args.Error != BluetoothError.Success && args.Status == GattServiceProviderAdvertisementStatus.Aborted)
                SetState(PeripheralState.Error, $"Bluetooth advertising stopped ({args.Error}).");
            else
                UpdateConnectionState();
        }

        private void OnSubscribersChanged(GattLocalCharacteristic sender, object args) => UpdateConnectionState();

        private void UpdateConnectionState()
        {
            if (_hid == null) return;
            bool connected = false;
            foreach (var c in _inputs)
                if (c.SubscribedClients.Count > 0) { connected = true; break; }

            if (connected)
            {
                SetState(PeripheralState.Connected, $"{ConnectedDeviceName ?? "iPhone"} connected");
                if (ConnectedDeviceName == null) _ = ResolveDeviceNameAsync();
            }
            else
            {
                ConnectedDeviceName = null;
                SetState(PeripheralState.Advertising, "Waiting for iPhone…");
            }
        }

        private async Task ResolveDeviceNameAsync()
        {
            try
            {
                foreach (var c in _inputs)
                {
                    if (c.SubscribedClients.Count == 0) continue;
                    var id = c.SubscribedClients[0].Session.DeviceId.Id;
                    using var device = await BluetoothLEDevice.FromIdAsync(id);
                    if (device != null && !string.IsNullOrWhiteSpace(device.Name))
                    {
                        ConnectedDeviceName = device.Name;
                        if (State == PeripheralState.Connected)
                            SetState(PeripheralState.Connected, $"{device.Name} connected");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read phone name", ex);
            }
        }

        private void SetState(PeripheralState state, string detail)
        {
            if (state == State && detail == Detail) return;
            State = state;
            Detail = detail;
            Log.Info($"State: {state} - {detail}");
            StateChanged?.Invoke(state, detail);
        }
    }
}
