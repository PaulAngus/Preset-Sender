# Preset Sender

Current source release: [0.1.4](docs/releases/0.1.4.md).

Version 0.1.3 restores standalone Windows packaging: the EXE contains both native Windows MIDI API files and automatically extracts verified copies into its own cache on Windows 11 25H2 or later. No separate MIDI API installer, companion files, runtime download or .NET installation is needed. Windows 10 uses hardware MIDI or user-installed loopMIDI; the bundled API is never extracted or loaded there. Compatible Windows MIDI Services must still be available for built-in virtual MIDI. Backend selection and extraction diagnostics are recorded at `%LOCALAPPDATA%\PresetSender\midi-backend.log`.

Shared MIDI implementation: see [dependency and setup notes](dependencies/midi/README.md). Current and legacy backends come from the `MightBeUseful.Midi` packages maintained in [MIDIHelperLibrary](https://github.com/PaulAngus/MIDIHelperLibrary); earlier implementation/package details below describe the pre-migration builds.

Startup detection runs off the UI thread, retries temporary service/transport failures up to three times, and allows ten seconds for discovery. **Refresh Devices** rechecks backend capabilities as well as ports, so a temporary fallback can recover without restarting. Refresh reuses any native probe still in progress instead of accumulating blocked calls. A genuinely unavailable service still falls back to legacy MIDI. The shared provider remains reusable; Preset Sender opts into its own bundled API directory rather than requiring the shared per-user installation.

For loopMIDI on older Windows, see the [setup instructions](#loopmidi-setup-on-older-windows).

A compact MIDI Program Change utility with its own settings, tests, branding and releases. This repository builds independently: its Windows and macOS MIDI backends, message encoder and platform configuration are included here. PresetMaestro is not required. The application remains named **Preset Sender**, although its repository is named **Preset-Forwarder**.

## Use

On Windows, copy just `PresetSender.exe` to the other PC and launch it. The executable includes the .NET runtime and Windows MIDI API components; it automatically unpacks them into the runtime's per-user cache. No companion files or application installer are required. Windows MIDI Services must still be available for the built-in virtual output.

1. Open `PresetSender.exe`, then click **Configure**. In **MIDI Connection**, select **Preset Sender (virtual device)** to publish an input that other apps on this PC can receive from. Click **Connect**, then select **Preset Sender** in the receiving app's MIDI input list. Alternatively, select an existing hardware or virtual output. An input controller is optional; the computer keyboard needs none. Connecting sends no program changes (virtual MIDI protocol discovery happens automatically). Connection settings and **Disconnect** remain visible.
2. In Configure → **Additional options**, set **Output channel** to a single channel (1–16) or **All channels**, and choose **Preset numbering**. All channels sends the same Program Change once on each of the 16 channels. This is the sending equivalent of the requested omni option; MIDI's formal Omni mode is a receiver setting. A receiver listening to all channels may process the change 16 times, so prefer a single channel for that receiver. The default is channel 1 and zero-based numbering: displayed `42` sends MIDI PC 42. One-based numbering sends PC 41 for displayed `42`.
3. Click a keypad button to focus entry, then type `42` and press **Enter**, or click **Send**. Top-row and numpad digits work. With Auto-send off, digits wait for confirmation. **Escape/Clear** discards the pending entry; **Backspace** removes its last digit. Successful sending clears entry and leaves the last sent preset on the display. The sent number, wire PC value and channel are also recorded in Configure → Additional options. A failed send preserves entry and disconnects the ports.
4. Choose **Digits** from **1 to 4** (default **3**) in **Entry Options** for both manual entry and auto-send. For example, 2 accepts `42`; 4 accepts `0042`. With **Auto-send** off, entry waits for Send/Enter and extra digits are rejected until you clear, edit or send. With it on, reaching the selected count starts the delay before sending. The MIDI PC range remains 0–127 or displayed 1–128.

The main screen copies PresetMaestro's sender: a connection label, large preset display, 1–9 keypad, CLR/0/SEND, and PREV/LAST/NEXT. PREV and NEXT send the adjacent program and wrap within 0–127; before the first send they select 127 and 0 respectively. LAST returns to the previous successfully sent program and can toggle back. The current amber logo and icons are retained. **Configure** opens the PresetMaestro-style **MIDI Connection** and **Entry Options** cards in a separate resizable window. Output channel, numbering and incoming MIDI forwarding are always shown in the **Additional options** card. Learned controls are in the **MIDI Note Mapping** window. **Save** writes configuration immediately and closes the page. **Cancel**, the window close control or Escape discards unsaved preferences and returns focus to Configure. Edits preview while the page is open; connection actions and MIDI already sent remain in effect. Opening Configure again brings that same window forward.

The preset display uses the selected **Digits** width before and after sending, including leading zeros (for example `07`, `007` or `0007`). PREV/NEXT/LAST and one-based numbering use the same formatting. Significant digits are never truncated if a navigated preset needs more characters than the selected width.

Computer keyboard entry works while this utility is focused, with focus in the keypad. Configuration controls keep their own keyboard behavior. MIDI controller input works while connected, including when another application has focus. No global keyboard hooks are installed.

## loopMIDI setup on older Windows

Use this to send Program Change messages to another application on a supported Windows version when the built-in virtual publisher is unavailable. The shared library normally chooses its legacy MIDI backend automatically on older Windows. This route needs loopMIDI's driver, but does not need Windows MIDI Services or the optional current API installer.

1. Download and install [loopMIDI from Tobias Erichsen](https://www.tobias-erichsen.de/software/loopmidi.html), then start it on the same PC as Preset Sender and the receiving application.
2. Create one port named `Preset Sender To App`.
3. Keep loopMIDI running in the tray. Enable its autostart option if the MIDI apps start at sign-in. Its ports exist only while it runs and are specific to the Windows user account.
4. In Preset Sender, open **Configure → MIDI connection**, click **Refresh Devices**, and select `Preset Sender To App` as the **output**. A MIDI input is optional when using the computer keyboard. Click **Connect**.
5. In the receiving application, enable `Preset Sender To App` as a **MIDI input**, enable Program Change reception, and assign its presets to program numbers if needed. For a plug-in, the host/DAW must route that input into the plug-in.

| Application | Selection |
|---|---|
| Preset Sender | MIDI **output**: `Preset Sender To App` |
| Receiving application / DAW | MIDI **input**: `Preset Sender To App` |

6. Start with **Output channel 1**, configure the receiver for channel 1 (or all channels), enter a known preset number and click **Send**. If the receiver selects the adjacent preset, check **Preset numbering** under Additional options: zero-based display `42` sends PC 42, while one-based display `42` sends PC 41. The receiver must have a matching preset assignment.

For an optional software MIDI controller, create a different cable, such as `Preset Sender Controls`; choose it as the controller's output and Sender's input. Keep the sending cable separate, and avoid routing MIDI echo/thru back into Sender, especially when **Forward incoming MIDI** is enabled.

**Ports missing or no preset change?** Create the port before opening the apps, click **Refresh Devices**, or restart a receiver that scans only at startup. Check that loopMIDI is still running, both apps use the same Windows account, the cable names match, the receiver accepts Program Change, and its MIDI channel and numbering agree. loopMIDI connects software on this PC; it does not create a hardware connection or a connection to another computer.

If you need to force the shared legacy backend, close Sender and run this PowerShell block from its executable folder:

```powershell
$env:MBU_MIDI_BACKEND = 'legacy'
.\PresetSender.exe --midi-backend | Out-Host
.\PresetSender.exe
```

The backend check should report **Legacy MIDI 1.0**. This affects apps launched from that shell. To return to automatic selection, run `Remove-Item Env:MBU_MIDI_BACKEND` in that shell and restart Sender. See the [shared MIDI dependency notes](dependencies/midi/README.md) for the common policy used by all three apps.

## Configuration and MIDI note mapping

The **MIDI Connection** card has MIDI In and its **Omni / 1–16** note-entry channel filter, MIDI Out, per-device **Thru In(s)** selections and channel filters, Connect, Disconnect, Refresh Devices and Diagnostics. Diagnostics shows the latest 500 MIDI messages and can clear the log.

**Thru In(s)** forwards unused MIDI channel messages to the selected output on their original channels. Notes used by the fixed preset-entry map are consumed, including their releases and note pressure. System messages, including SysEx and clock/transport, are excluded. A thru input's channel selector filters note entry only. Thru inputs can be enabled or disabled while connected. The main input and same-name output are excluded from the thru list. Disabling a thru input or disconnecting releases notes held through that input. Avoid connecting the output back into a thru input.

In **Entry Options**, **Keyboard entry** and **MIDI note entry** act independently; the on-screen keypad remains available. **Auto-send** waits until the selected **Digits** count (1–4, default 3) is entered, then waits for **Delay** (default 35 ms, range 10–2000 ms) before sending a valid preset. Leading zeros count as digits: `0042` waits for four digits and sends PC 42 with zero-based numbering. Clear, Backspace, manual Send, navigation, opening Configure and changing Digits cancel a pending auto-send. When auto-send is enabled, typing beyond the selected count starts a new entry and cancels the older pending send.

**MIDI note mapping → Configure…** opens the 15-action map. Select a digit or command, enter a unique MIDI note from 0 to 127, then **Save mapping**. Cancel or Escape discards edits and returns focus. Default assignments are:

| Notes | Actions |
|---|---|
| 36–44 | Digits 1–9 |
| 45 | Digit 0 |
| 46 / 47 | SEND / CLEAR |
| 48 / 49 / 50 | NEXT / PREV / LAST |

The map applies to positive-velocity Note On messages from MIDI In and selected thru inputs, including repeated presses without a note-off. Note-off and velocity-zero Note On do not trigger entry. Existing learned controls take priority on the main input for their matching channel/note; their release behavior remains unchanged.

## MIDI controllers

Open **Configure → MIDI note mapping → Configure…**, then scroll to **MIDI learning**. Choose a digit, **Send**, or **Clear** in **Command to learn**, click **Learn**, and press the corresponding controller button. Repeat for the commands you need. Notes, control changes (CC), and program changes (PC) can all be learned; the exact incoming MIDI channel is part of the mapping. **Input channel** can additionally restrict reception, or accept any channel. Learning consumes the incoming press and sends nothing. **Cancel learn** or Escape exits learning. **Remove mapping** removes the selected command's assignment. Learning and removal preview immediately and are retained when the note-map dialog is cancelled. **Save mapping** applies the fixed note assignments to the configuration preview. Use configuration **Save** to persist all these edits; configuration **Cancel** restores the previous settings, mappings and learned controls. While waiting for a control, Escape cancels learning and leaves the mapping window open; another Escape closes it.

For learned controls, nonzero note-on and CC values count as presses; release with note-off, velocity-zero note-on, or CC value zero before pressing again. Repeated nonzero values are ignored until release. For controllers that only emit nonzero CC values, use their momentary press/release mode or PC messages. PC mappings fire on every incoming PC, since PC has no release message. Mapping a new control replaces that command's previous mapping and any command previously using the same control.

**Forward incoming MIDI** optionally passes unused messages from the main MIDI In to MIDI Out unchanged, preserving their original channels and order. It includes Program Change, Control Change (including bank selection), Note On/Off, pitch bend, channel pressure and polyphonic aftertouch. SysEx and all system messages, including clock/transport and song position, are excluded. The input channel filter applies to preset entry and learned controls; unused messages on other channels can still pass through. Output channel / All channels applies to presets generated by Preset Sender, not to forwarded messages. Forwarding leaves pending entry and preset history intact.

Mapped preset-entry notes and learned note/CC/PC controls take precedence and are consumed, including repeated values, releases and pressure for consumed notes. A control captured during learning is consumed as well. Consumed note/CC releases stay suppressed if the mapping is removed while held. Forwarded notes are released when forwarding is disabled, a thru input is removed, the app disconnects or a held note becomes an entry/learning control. The same exclusion rules apply to preset-entry notes on selected Thru In(s) inputs; learned controls and learning use the main MIDI In. Avoid routing the output back into an input, which would create a MIDI feedback loop.

Forwarding remains off by default. Saved **Forward incoming program changes** preferences migrate to the new option; an explicit saved `ForwardMidi` value takes precedence.

## AmpliTube and other applications

On Windows 11 25H2 or later with compatible, updated Windows MIDI Services components, **Preset Sender (virtual device)** publishes a named MIDI source while connected. Other apps select **Preset Sender** as an **input**, including legacy MIDI 1.0 apps when Windows MIDI Services' legacy API integration is enabled. The internal **Preset Sender (Relay)** port also appears; choose the name without that suffix. A symmetric Windows loopback pair avoids the virtual-device cleanup path that stalled MidiSrv during coexistence testing. If a receiver only discovers ports at startup, connect Preset Sender before starting that receiver. Disconnecting or closing Preset Sender removes both ports. Only one instance can publish this device at a time. No third-party virtual MIDI driver is installed.

This implementation uses Microsoft's **Windows MIDI Services preview 10 API** (`0.99.88-preview.10`). Consult the [Microsoft minimum requirements](https://microsoft.github.io/MIDI/kb/minimum-requirements/) and [preview release notes and distribution terms](https://github.com/microsoft/MIDI/releases/tag/inbox-preview-10). Public distribution of the preview API requires the developer to meet those terms, including updates as the API changes. The standalone executable embeds its MIDI DLL and resource file and automatically extracts them into the runtime cache. The existing hardware-output path remains usable on older Windows versions.

**Native validation (11 October 2026):** the native integration check passed with the symmetric loopback publisher: two simultaneous legacy MIDI receivers both received PC 42 on channel 6 and PC 127 on each of channels 1–16, and the endpoints disappeared on disconnect. The published sender also passed MIDI Forwarder 1.0.2's coexistence check: both startup orders, two receivers on all channels, three forwarder start/stop cycles, bidirectional forwarding/SysEx and clean sender shutdown. No physical hardware was used in that check. Specific target-app preset mapping, such as AmpliTube, remains a separate setup step. The isolated sender host limits an unresponsive-service connection attempt to 15 seconds.

For hardware, select the device's USB MIDI output or the output of its MIDI interface. A software virtual source does not automatically route to physical MIDI cables or other computers. On macOS, use an existing virtual port such as an IAC bus; the built-in publisher is Windows-only. The target must accept standard MIDI 1.0 Program Change messages and listen on the selected channel.

In AmpliTube, assign the desired presets to MIDI program numbers and enable the corresponding MIDI input. IK documents MIDI preset switching in AmpliTube 5's standalone live mode. For a plug-in, your DAW must route MIDI into the plug-in; availability depends on the host and plug-in format. See [IK's AmpliTube specifications](https://www.ikmultimedia.com/products/amplitube5/?p=specs) and [IK's MIDI routing FAQ](https://www.ikmultimedia.com/support/faq/index.php?topic=amplitube-4) (the routing FAQ refers to AmpliTube 4).

The entered number is encoded as one standard MIDI program value. It sends only PC; it does not send bank-select CCs, read preset names, or configure the receiving app's preset assignments.

Configuration **Save** persists preferences immediately; Cancel/window close/Escape discard unsaved edits. Connection actions remain live. Settings are stored separately under `%APPDATA%\PresetSender\settings.json` on Windows, or the platform's application-data directory on macOS. Ports, virtual-output choice, numbering, channels, learned commands, entry switches, auto-send digit count and delay, note map and thru selections are retained. Output channel `0` stores All channels. Startup restores selections but stays disconnected. Existing saved hardware selections remain selected; new Windows configurations default to the built-in virtual device. A missing saved output requires an explicit new selection. Settings without an auto-send digit count default to three.

## Develop and package

From the repository root with .NET 10 on Windows:

```powershell
./Initialize-MidiSdk.ps1
dotnet build PresetSender.slnx -c Release
dotnet run --project PresetSender.csproj
dotnet test PresetSender.slnx -c Release
dotnet format PresetSender.slnx --verify-no-changes
./Publish-PresetSender.ps1
```

The initialization script downloads a pinned, checksum-verified Microsoft preview NuGet package into the ignored `packages/` feed. Run it before the first Windows build on a fresh checkout; the publish script runs it automatically and does not install the API on the developer PC. The publish script creates a standalone Windows x64 executable at `artifacts/windows/PresetSender.exe`. The two MIDI API files are managed assembly resources, so .NET does not automatically extract them on legacy Windows. On eligible Windows 11, the app verifies and extracts them into `%LOCALAPPDATA%/PresetSender/Midi/windows-midi/0.99.88-preview.10/x64` (under `DOTNET_BUNDLE_EXTRACT_BASE_DIR` when set), then configures the shared provider to load that directory. Damaged cached files are repaired from the embedded resources, and concurrent starts share the same verified files. The virtual MIDI subprocess launches the original executable rather than looking in the extraction cache. It requires no separately installed .NET runtime.

Normal tests simulate MIDI and do not contact hardware or create system endpoints. The native check covers two simultaneous legacy MIDI receivers, channel 6 individually, all sixteen wire channels, note-on/CC/note-off bytes and endpoint removal. To run it explicitly:

```powershell
$env:PRESET_SENDER_NATIVE_MIDI_TEST = '1'
dotnet test PresetSender.slnx -c Release --filter FullyQualifiedName~VirtualMidiIntegrationTests
Remove-Item Env:PRESET_SENDER_NATIVE_MIDI_TEST
```

To test the packaged executable, also set `PRESET_SENDER_TEST_EXE` to its absolute path before running the native check. Copy only the EXE into an empty directory and set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a fresh cache directory to check extraction. On 12 October 2026, all 26 tests passed using that setup, including two MIDI receivers, all sixteen channels, and endpoint removal. Both MIDI API files were extracted from the EXE. This confirms standalone packaging on the development PC; the other PC still needs compatible Windows MIDI Services.

The packaging uses Microsoft's [single-file deployment and full extraction guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) for .NET and Avalonia. Native MIDI extraction is separately gated by the OS check and loaded explicitly through the shared provider.

On macOS, run `dotnet build PresetSender.slnx -c Release`, `dotnet test PresetSender.slnx -c Release`, or `dotnet run --project PresetSender.csproj` directly; the Windows MIDI initialization script is unnecessary. Platform selection follows the host or runtime identifier, with `-p:SenderPlatform=macOS` available for explicit selection. Native Mac launch and MIDI acceptance remain unverified on this Windows development PC.

The repository includes Windows and macOS CI jobs for builds, simulated tests and formatting. The Windows job also packages the standalone EXE. Native MIDI checks remain opt-in and are not run in CI.

## Repository migration

Preset Sender moved from PresetMaestro's `PresetSender/` directory on 12 October 2026. Its committed directory history was extracted with `git subtree split`, retaining authorship, dates and changes at this repository's root. The current uncommitted sender changes were carried across before separating the builds. The small MIDI transport, encoder and platform adapters were copied into `Midi/` and `Platforms/`; their namespaces and platform symbols now belong to Preset Sender. Build rules, formatting settings and the Apache 2.0 license are local to this repository.

Windows settings retain `%APPDATA%\PresetSender\settings.json`, so moving repositories does not reset the user's configuration. The PresetMaestro application, including its own Preset Sender page, remains separate.

Migration validation on 12 October 2026: a fresh local clone restored and built the Windows and macOS targets, passed all 33 simulated Windows tests and formatting verification, and published the standalone Windows EXE. The main checkout passed all 34 Windows tests with native MIDI enabled and an EXE-only test directory using a fresh extraction cache. PresetMaestro's Release build also passed after the old sender directory was removed. The macOS target was compiled on Windows; native Mac execution and the new CI jobs have not yet been run.

UX decisions and verification are recorded in [ux.md](ux.md).

## Automated testing

For unattended functional, native MIDI and packaged Windows desktop testing, run
`pwsh -NoProfile -File ./Test-PresetSender.ps1`. See [the testing pilot guide](docs/testing-pilot.md)
for the reports, isolated settings and setup on Windows 10/11 and macOS.

## Logo and icon

The selected **Amber Gradient** logo appears in the window/executable icon: an amber-to-orange effects pedal with a dark forwarding arrow and shaded footswitch. Website-ready transparent PNGs and a favicon are in [Branding/Website](Branding/Website/README.md). The executable's multi-resolution icon is `Assets/preset-sender.ico`; the selected original is retained under [Branding/NewOptions/PedalGradients](Branding/NewOptions/PedalGradients/README.md). Re-export sizes on Windows with `Branding/Export-BrandAssets.ps1` before building if the source artwork changes.

On 14 October 2026, the former DIN Send socket mark was assigned to the sibling MIDI Forwarder utility, and the user selected Amber Gradient for this preset app. [The logo exploration](Branding/NewOptions/README.md) retains the ten starting concepts, colour studies and gradient refinements.

## Offline Windows Test Lab profile

Build an audited bundle without MIDI access:

```text
python packaging/build_test_lab.py --rid win-x64 --config packaging/test-lab-offline-windows.json --output artifacts/offline-windows
```

Every bundle uses fresh build outputs and records its selected test-source hashes in
`offline-policy.json`. All covers only the profile's offline Functional stages.
Native and interactive Desktop requests report unavailable; no hardware or MIDI
routing qualification is implied. No native, virtual, TCP or UDP MIDI connections
are opened or created. In-memory logic fixtures remain included. The normal
development profile and its broader transport tests remain separate.
