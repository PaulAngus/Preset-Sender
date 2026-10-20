# Preset Sender automated test pilot

The pilot can now be controlled by the separate `crossplatform-testing`
dashboard. Pair the portable agent once, leave it running, then start suites
and collect reports centrally. See [the dashboard adapter notes](test-lab-dashboard.md).

Test machines must use the **prebuilt portable bundles** described in
[packaging/pilot/README.md](../packaging/pilot/README.md). They require no installed
.NET runtime/SDK, PowerShell or Git. The development-machine runner below remains
available on the Windows development PC and Mac development VMs only.

Run from a checkout containing the pilot changes, using PowerShell 7.2+ and the
.NET 10 SDK. No ChatGPT desktop control is needed to execute the tests.

On Windows, every suite first prepares the checksum-verified private MIDI build
feed automatically, including on a fresh checkout with an empty NuGet cache.
This downloads a build dependency only; it does not install a MIDI runtime.
macOS suites do not run the Windows initialization step.

```powershell
pwsh -NoProfile -File ./Test-PresetSender.ps1
```

The default `All` run builds a private Release executable, reuses the existing
functional tests, runs available native MIDI tests, and runs the Windows desktop
scenarios. Results go to a new `artifacts/test-pilot/<run-id>/` directory.
Open `report.html`; `report.json` is the machine-readable equivalent. Every run
retains source hashes (including uncommitted files), the app executable hash, the
MIDI package version, real OS details, TRX results, process logs and screenshots.
Build and bundle caches also remain in the run directory for investigation.

Exit codes: **0** all requested stages passed; **1** failure; **2** requested
coverage unavailable. Missing machines, native capabilities or desktop adapters
are never counted as successful coverage. Invalid invocation/setup can fail
before a run directory is created. Each invocation requires a fresh directory.
Only one pilot run per checkout can hold `artifacts/test-pilot/runner.lock`.

## Useful commands

```powershell
# Fast functional/keyboard/configuration/routing tests, no native ports.
pwsh -NoProfile -File ./Test-PresetSender.ps1 -Suite Functional

# Published EXE extraction/repair and virtual MIDI integration on eligible Windows.
pwsh -NoProfile -File ./Test-PresetSender.ps1 -Suite Native

# Real packaged Windows UI plus an independent MIDI receiver process.
pwsh -NoProfile -File ./Test-PresetSender.ps1 -Suite Desktop

# Windows legacy backend or Mac IAC loopback; create this dedicated test port first.
pwsh -NoProfile -File ./Test-PresetSender.ps1 -LoopbackPort PresetSender-Test

# Results may be placed outside the checkout. The directory must not exist yet.
pwsh -NoProfile -File ./Test-PresetSender.ps1 -ResultsDirectory /absolute/new/run
```

The runner uses platform-selected projects on the actual host. Functional and
loopback test support is implemented for Windows/macOS. Native Mac execution
must still be verified on a Mac. Windows desktop automation uses FlaUI.UIA3
5.0.0; there is currently no Mac desktop adapter. `All` on Mac therefore reports
incomplete, while `Functional` or `Native -LoopbackPort PresetSender-Test` can
complete their requested coverage independently.

## What the pilot verifies

- Reuses the existing headless tests for numbering, keyboard and MIDI entry,
  all-channel output, configuration, learned controls, routing, auto-send,
  partial failures and persistence. Their fake ports were extracted into
  `Tests/Support/CapturedMidiBackend.cs` for reuse. Adds input-error/late-message
  cleanup coverage and isolated-settings path validation.
- Eligible Windows MIDI Services machines reuse all three opt-in native tests:
  bundled API extraction and cache repair; missing API diagnostics; all-channel
  delivery to two legacy receivers and virtual endpoint cleanup. The runner
  explicitly enables them against the newly built EXE and rejects skipped tests.
- A configured legacy/IAC loopback uses the independent probe's literal PC/note
  vectors through real OS MIDI input and output.
- The Windows desktop suite starts the packaged EXE, opens configuration, connects
  its test output, uses the keypad to send bank select `B5 00 00` then `C5 2A`, changes output to all channels,
  returns to keyboard entry and sends program 127 on all 16 channels, restarts,
  verifies saved settings by sending program zero, and checks virtual-port removal.
  Received messages are recorded in JSON Lines by a separate receiver process;
  exact sequences are compared, including a short window for duplicate arrivals.

### Preset outcome coverage and review (20 October 2026)

`BankSelectionTests` models an independent receiver using the FM9's documented
CC#0 bank and Program Change rules ([FM9 manual, MIDI reference tables](https://www.fractalaudio.com/downloads/manuals/FM9/FM9-Owners-Manual.pdf)).
It checks the selected preset, not just outgoing bytes: all 128 supported presets,
both numbering settings, starting banks 0, 1, 2, 3 and 127, channels 1, 6 and 16,
and all-channel output. Each request starts with the receiver's bank re-seeded,
including Next/Prev/Last and boundary wrapping. Failure checks cover both the bank
message and the subsequent program message. These tests are also included in the
explicit offline/physical-read-only portable test profiles; they use fake ports.

The new bank tests failed against the old implementation (33 failures, 9 passes)
and passed after the fix (42 passes). Further review reproduced eight failures:
navigation discarded pending entry while disconnected or after a send error, and
Connect used stale numeric output indices after devices moved or disappeared.
Those cases now have passing regressions. Navigation clears entry only on a
successful send; Connect resolves the saved name/occurrence in the current catalog
and refuses a missing selection. Names/occurrences still cannot distinguish two
identically named devices that exchange order; a stable output-device identity
would require extending the backend boundary.

The complete Release functional suite passes: 152 tests, none skipped. Evidence
is in `artifacts/bank-selection-tests/` (`bank-before.trx`, `bank-after.trx`,
`review-before.trx`, `functional.trx`). The desktop pilot's expected sequences
were updated for bank selection. This run does not establish physical FM9
selection/readback, native transport, or packaged desktop acceptance.

Headless checks exercise application controls and routing in memory. Native
checks and the desktop suite add coverage of the actual OS MIDI/window backends.
The desktop run requires an unlocked, interactive Windows desktop. It briefly
focuses its own windows and types digits; do not use that desktop concurrently.
FlaUI uses stable automation identifiers rather than screen coordinates.

## Isolation

The runner never restores, resets, stages or commits the working tree. It builds
the current working files and records their hashes. It does not call the normal
distribution scripts or replace installed executables.

Each child process has its own `PRESET_SENDER_DATA_DIRECTORY`, screenshot paths
and bundle cache. Production settings paths remain unchanged when that variable
is absent. The override must be an absolute path. Native backend diagnostics
also use the overridden directory. Backend/API override environment variables
are cleared only for the child processes, leaving the invoking shell unchanged.

The desktop test seeds a new profile with keyboard-only input, MIDI thru disabled,
and only the built-in virtual output or an explicitly named test loopback output.
The desktop run publishes a unique `PresetSender-Test-<run-id>` virtual endpoint
and a name-scoped mutex. Its receiver refuses ambiguous or existing endpoints.
This opt-in name override requires an isolated absolute data directory; ordinary
Sender instances retain their normal name and mutex. The reused native tests
still use the ordinary endpoint name, so close other Sender instances before a
full/native run.
External loopback ports must start with `PresetSender-Test`; they must be dedicated
software loopbacks, not aliases routed to hardware. No physical device is required.

The receiver has a bounded lifetime; processes started by the harness are closed
or terminated on failure. Logs and the desktop case report survive failures.
Historical run folders are retained; remove them yourself when evidence is no
longer needed. A run builds a test candidate, not a public release.

## Setup on the other machines

Transfer and extract the prebuilt platform ZIP. Do not install .NET or PowerShell
on test machines. All building and package restore happen on development machines;
the published executables carry their private application runtime components.
See the ZIP's `START-HERE.html` for the launchers and reports.

| Machine | Setup | First command |
| --- | --- | --- |
| Other Windows 11 | Extract the Windows ZIP. If MIDI Services is unavailable, create a dedicated `PresetSender-Test` loopMIDI port. Stay logged in/unlocked for desktop tests. | `Setup-Windows.cmd`, then `Run-Windows.cmd` |
| Windows 10 | Extract the Windows ZIP. Install/start loopMIDI and create `PresetSender-Test`. | Same launchers |
| Native macOS 26/27 | Copy the whole Mac folder after signing/preparing it once on the Mac development VM. Enable IAC Driver and create an online `PresetSender-Test` bus in each boot installation. | `Setup-Mac.command`, then `Run-Mac.command` |
| Parallels macOS 27 development VM | Prepare/sign the Mac bundle using the included development command, validate it here, then transfer the prepared folder to the test Macs. | `Prepare-on-Mac-DEV.command` |

The Mac desktop adapter, signing/notarization/Finder checks, Explorer-launch
qualification, physical Fractal readback, lab-wide scheduling and multi-machine
aggregation remain subsequent work. This pilot does not claim those checks ran.
The dual-boot Mac runs one OS at a time; retain separate reports for both boots.

For local development-machine runs, invoke the same script and retain
the run folder's `report.*`, `source.json`, `*.txt`, `functional/`, `native/`,
`screenshots/` and `desktop/` directories as evidence. Build caches and private
executables need not be included in shared evidence. Use interactive user sessions
for desktop checks; functional checks can run without an unlocked desktop.
No hosted runner is used.

## Desktop keyboard synchronization

The desktop driver activates its own title bar when Windows denies a background
agent foreground activation, waits for the test window to own the foreground, and verifies
that closing configuration restores focus to Configure. It sends a press/release
pair for each physical digit key, waits for the displayed entry after each digit,
and releases Enter after confirmation. It fails explicitly if the foreground is
lost before sending keys; it never retries a potentially delivered MIDI command.
This preserves the product's existing focus behavior from
[Fluent dialog guidance](https://fluent2.microsoft.design/components/web/react/core/dialog/usage/).
FlaUI's [keyboard API](https://github.com/FlaUI/FlaUI/blob/v5.0.0/src/FlaUI.Core/Input/Keyboard.cs)
distinguishes `Press` (held key) from `Type` (press and release).
