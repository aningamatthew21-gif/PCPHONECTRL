# PhoneCtrl

Control your **iPhone from your Windows PC over Bluetooth** with a small widget on the screen edge.
Hover over it to open it, then click to:

| Button | What happens on the iPhone |
|---|---|
| ⏮ ⏯ ⏭ | Previous song · play/pause · next song (Spotify, Apple Music, YouTube Music, …) |
| 🔉 🔊 | Volume down / up |
| ▲ ▼ | Previous / next video in TikTok, Instagram Reels, YouTube Shorts |
| 🔒 Lock phone | Locks the phone, so the screen goes black |

No dongle, no Wi-Fi, no app on the phone. Your PC's own Bluetooth pretends to be a
**keyboard + mouse + media remote**, which iOS supports natively.

## Privacy and security

* **One-way only.** The PC can only *send* button presses. It can't read anything from the phone:
  no photos, messages or notifications.
* **No network.** Nothing goes over Wi-Fi or the internet, so the office network sees nothing.
* **Encrypted and paired.** The phone must pair with the PC first (Bluetooth LE Secure Connections).
  Your phone's passcode still protects it when it's locked.

## Requirements

* Windows 10 (2004) or Windows 11 with Bluetooth that supports the **LE peripheral role**.
  Check with BleHid's `BleHid.Cli.exe --diagnose` and look for `Peripheral role : True`.
* An iPhone. Tested on an iPhone 15.

## Install

1. Open the repository's **Releases** page on GitHub → **Latest build** → under *Assets* download
   **PhoneCtrl-win-x64.zip** → right-click → **Extract All…**
2. Put `PhoneCtrl.exe` anywhere (for example `Documents\PhoneCtrl`) and double-click it.
   Windows SmartScreen may warn because the exe is unsigned: **More info → Run anyway**.

## First-time setup

1. **Close BleHid** if it's running. Only one app at a time can be the Bluetooth keyboard.
2. On the iPhone: **Settings → Bluetooth** → tap ⓘ next to your PC → **Forget This Device**.
   This is required once, because PhoneCtrl adds media keys and the phone has to learn the new layout.
3. Start **PhoneCtrl.exe**. A slim tab appears on the right edge of the screen. Its dot shows the status:
   * 🟠 waiting for iPhone · 🟢 connected · 🔴 problem (hover for details)
4. iPhone: **Settings → Bluetooth** → tap your PC under *Other Devices* → **Pair**.
5. iPhone: **Settings → Accessibility → Touch → AssistiveTouch → On** (needed for scrolling and lock).
6. Set up the lock button:
   **AssistiveTouch → Pointer Devices → Devices → (your PC) → Customize Additional Buttons…**
   When it asks you to press a button, click **🔒 Lock phone** in the widget, then choose **Lock Screen**.
7. Open TikTok/Reels and try ▼. If it goes the wrong way, see *Settings* below.

## Keyboard shortcuts (work from any app)

| Shortcut | Action |
|---|---|
| **Ctrl + Alt + L** | Lock phone (boss key) |
| Ctrl + Alt + PageDown / PageUp | Next / previous video |
| Ctrl + Alt + End / Home | Next / previous song |
| Ctrl + Alt + Space | Play / pause |
| Ctrl + Alt + Num+ / Num− | Volume up / down |

## Using the widget

* **Hover** the tab to open it. Move away and it closes.
* **Drag** the tab (or the panel's top row) to move it. It snaps to the nearest left/right edge and works on any monitor.
* **Tray icon** (near the clock): show/hide the widget, restart Bluetooth, open settings or logs, exit.
* The ⌖ button in the panel's top row moves the phone's pointer to the middle of the screen
  (scrolling acts on whatever is under the pointer).

## Settings

Tray icon → **Open settings file**, edit, save, then tray icon → **Reload settings**.

| Setting | Default | Meaning |
|---|---|---|
| `ScrollNotches` | `1` | Wheel notches per ▲/▼ click. Raise it if a video doesn't change. |
| `InvertScroll` | `false` | Set to `true` if ▼ goes to the *previous* video. |
| `LockMethod` | `"MouseButton"` | `"PowerKey"` tries a media power key instead (experimental). |
| `LockMouseButton` | `3` | Mouse button sent for lock (must match your AssistiveTouch mapping). |
| `CenterOffsetX/Y` | `200` / `400` | Tune the ⌖ "center pointer" position. |
| `HotKeys` | see above | Change or clear (`""`) any shortcut. |

## Troubleshooting

* **Red dot / "can't act as a keyboard"**: your Bluetooth chip lacks the LE peripheral role. A Bluetooth 5.0 USB adapter fixes it.
* **iPhone doesn't see the PC**: tray → **Restart Bluetooth**, then toggle Bluetooth off/on on the phone.
* **Was working, now nothing happens**: on the iPhone, *Forget This Device*, then pair again.
* **Logs**: tray → **Open log folder** (`%LOCALAPPDATA%\PhoneCtrl\phonectrl.log`).

## How it works

`src/PhoneCtrl/Ble/HidPeripheral.cs` publishes a Bluetooth LE **HID over GATT** service using the
in-box Windows `GattServiceProvider` API. The HID report map (`Ble/HidReports.cs`) declares a keyboard,
a 5-button mouse with wheel, and consumer-control media keys. Widget buttons send:

* media keys → Next/Previous Track, Play/Pause, Volume (consumer page usages 0xB5, 0xB6, 0xCD, 0xE9, 0xEA)
* ▲/▼ → mouse wheel
* Lock → a mouse button press that AssistiveTouch maps to *Lock Screen*

## Roadmap ideas

* Mouse pass-through: move the PC mouse past the screen edge onto the phone
* In-app settings screen and custom buttons
* Mac / Linux versions of the widget
