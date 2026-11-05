namespace PhoneCtrl.Ble
{
    /// <summary>
    /// HID report descriptor ("report map") that the iPhone reads when it pairs.
    /// It describes three virtual devices in one:
    ///   Report ID 1 - keyboard (8 bytes: modifiers, reserved, 6 key codes)
    ///   Report ID 2 - mouse    (4 bytes: buttons, x, y, wheel)
    ///   Report ID 3 - media / consumer-control keys (1 byte bitmap)
    /// </summary>
    internal static class HidReports
    {
        public const byte KeyboardId = 1;
        public const byte MouseId = 2;
        public const byte ConsumerId = 3;

        public static readonly byte[] ReportMap =
        {
            // ---- Keyboard ----
            0x05, 0x01,       // Usage Page (Generic Desktop)
            0x09, 0x06,       // Usage (Keyboard)
            0xA1, 0x01,       // Collection (Application)
            0x85, KeyboardId, //   Report ID (1)
            0x05, 0x07,       //   Usage Page (Key Codes)
            0x19, 0xE0,       //   Usage Minimum (Left Control)
            0x29, 0xE7,       //   Usage Maximum (Right GUI)
            0x15, 0x00,       //   Logical Minimum (0)
            0x25, 0x01,       //   Logical Maximum (1)
            0x75, 0x01,       //   Report Size (1)
            0x95, 0x08,       //   Report Count (8)
            0x81, 0x02,       //   Input (Data, Variable, Absolute)  ; modifier byte
            0x95, 0x01,       //   Report Count (1)
            0x75, 0x08,       //   Report Size (8)
            0x81, 0x01,       //   Input (Constant)                  ; reserved byte
            0x95, 0x05,       //   Report Count (5)
            0x75, 0x01,       //   Report Size (1)
            0x05, 0x08,       //   Usage Page (LEDs)
            0x19, 0x01,       //   Usage Minimum (Num Lock)
            0x29, 0x05,       //   Usage Maximum (Kana)
            0x91, 0x02,       //   Output (Data, Variable, Absolute) ; LED report
            0x95, 0x01,       //   Report Count (1)
            0x75, 0x03,       //   Report Size (3)
            0x91, 0x01,       //   Output (Constant)                 ; LED padding
            0x95, 0x06,       //   Report Count (6)
            0x75, 0x08,       //   Report Size (8)
            0x15, 0x00,       //   Logical Minimum (0)
            0x25, 0x65,       //   Logical Maximum (101)
            0x05, 0x07,       //   Usage Page (Key Codes)
            0x19, 0x00,       //   Usage Minimum (0)
            0x29, 0x65,       //   Usage Maximum (101)
            0x81, 0x00,       //   Input (Data, Array)               ; key array (6 keys)
            0xC0,             // End Collection

            // ---- Mouse ----
            0x05, 0x01,       // Usage Page (Generic Desktop)
            0x09, 0x02,       // Usage (Mouse)
            0xA1, 0x01,       // Collection (Application)
            0x85, MouseId,    //   Report ID (2)
            0x09, 0x01,       //   Usage (Pointer)
            0xA1, 0x00,       //   Collection (Physical)
            0x05, 0x09,       //     Usage Page (Buttons)
            0x19, 0x01,       //     Usage Minimum (1)
            0x29, 0x05,       //     Usage Maximum (5)
            0x15, 0x00,       //     Logical Minimum (0)
            0x25, 0x01,       //     Logical Maximum (1)
            0x95, 0x05,       //     Report Count (5)
            0x75, 0x01,       //     Report Size (1)
            0x81, 0x02,       //     Input (Data, Variable, Absolute) ; 5 buttons
            0x95, 0x01,       //     Report Count (1)
            0x75, 0x03,       //     Report Size (3)
            0x81, 0x01,       //     Input (Constant)                 ; padding
            0x05, 0x01,       //     Usage Page (Generic Desktop)
            0x09, 0x30,       //     Usage (X)
            0x09, 0x31,       //     Usage (Y)
            0x09, 0x38,       //     Usage (Wheel)
            0x15, 0x81,       //     Logical Minimum (-127)
            0x25, 0x7F,       //     Logical Maximum (127)
            0x75, 0x08,       //     Report Size (8)
            0x95, 0x03,       //     Report Count (3)
            0x81, 0x06,       //     Input (Data, Variable, Relative)
            0xC0,             //   End Collection
            0xC0,             // End Collection

            // ---- Media keys (Consumer Control) ----
            0x05, 0x0C,       // Usage Page (Consumer)
            0x09, 0x01,       // Usage (Consumer Control)
            0xA1, 0x01,       // Collection (Application)
            0x85, ConsumerId, //   Report ID (3)
            0x15, 0x00,       //   Logical Minimum (0)
            0x25, 0x01,       //   Logical Maximum (1)
            0x75, 0x01,       //   Report Size (1)
            0x95, 0x08,       //   Report Count (8)
            0x09, 0xB5,       //   Usage (Scan Next Track)      bit 0
            0x09, 0xB6,       //   Usage (Scan Previous Track)  bit 1
            0x09, 0xCD,       //   Usage (Play/Pause)           bit 2
            0x09, 0xE9,       //   Usage (Volume Increment)     bit 3
            0x09, 0xEA,       //   Usage (Volume Decrement)     bit 4
            0x09, 0xE2,       //   Usage (Mute)                 bit 5
            0x09, 0x30,       //   Usage (Power)                bit 6
            0x0A, 0x23, 0x02, //   Usage (AC Home)              bit 7
            0x81, 0x02,       //   Input (Data, Variable, Absolute)
            0xC0,             // End Collection
        };
    }

    /// <summary>Bits of the consumer-control (media key) report.</summary>
    public enum MediaKey : byte
    {
        NextTrack = 1 << 0,
        PreviousTrack = 1 << 1,
        PlayPause = 1 << 2,
        VolumeUp = 1 << 3,
        VolumeDown = 1 << 4,
        Mute = 1 << 5,
        Power = 1 << 6,
        Home = 1 << 7,
    }
}
