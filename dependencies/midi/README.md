# Pinned shared MIDI dependency

These first-party packages come from the independently buildable [MIDIHelperLibrary repository](https://github.com/PaulAngus/MIDIHelperLibrary). Package and namespace names remain `MightBeUseful.Midi`.

The selected family is exactly **0.3.6**: Midi, Windows, CoreMidi and Rtp.
All four archives are byte-for-byte identical to the release bundled by AI Toolkit
for Fractal and MIDI Forwarder. `packages.sha256` pins their archive bytes;
`source-manifest.json` records the source baseline and reviewed local source hashes.
Licences and notices remain inside the archives, including the RTP attribution.

`MidiVersion.props` supplies the plain release value for packaging filenames and
the exact `SharedMidiPackageVersion` range `[0.3.6]`. All application and helper
MIDI references use this range, including the Mac RTP dependency. NuGet source
mapping routes `MightBeUseful.Midi` and `MightBeUseful.Midi.*` exclusively to this
bundled feed. Other sources retain their existing third-party roles. The local
`.build/nuget-contained` cache isolates normal builds from unrelated global cache
entries. Source-development overrides remain explicit.

Normal builds need no sibling checkout or externally published first-party feed.
Update the complete package family, exact version property, source provenance and
archive hashes together from one reviewed library release; do not copy MIDI
implementation source into the application.

MBU_MIDI_BACKEND=auto/current/legacy controls the shared Windows policy. Preset Sender 0.1.3 bundles its own native API and configures the shared provider's app-local directory on Windows 11 25H2+: users copy only the EXE. Windows 10 uses hardware or user-installed loopMIDI. The shared library still supports optional setup for other consumers via ../../Initialize-SharedMidi.ps1 -EnableWindowsServices. Preset Sender's Initialize-MidiSdk.ps1 prepares the verified build feed without installing runtime components. Application --midi-backend and --list-ports commands report the same provider policy; MBU_MIDI_API_DIRECTORY remains an explicit diagnostic override.

Release 0.3.6 adds the shared diagnostic RTP responder; production backends and
CC/PC encoding retain their behavior. This update preserves application routing,
backend selection, failure/reconnection and packaging policies. It does not select
an RTP route or enable live-device checks. Native hardware validation is separate.

## Offline validation (Windows, 2026-10-18)

Windows: **81 passed, 3 native tests skipped**. Managed Mac:
**80 passed**. Physical MIDI opt-ins were disabled. Pilot helper builds
passed for Windows and Mac; the portable test helper builds for Windows x64 and
Mac arm64. No helper or published application was launched.

Static PE resource inspection verified the embedded Windows MIDI API DLL and PRI
against the verified SDK archive. The existing app-private extraction and Windows
version gates remain in application code without changes.

Self-contained win-x64, osx-arm64 and osx-x64 publications passed. They used fresh
repository caches and local sources only: first-party MIDI archives from this
bundled feed, existing third-party/runtime packages from the local package cache.
Publication outputs were isolated for verification. Payload inspection confirmed
exact MIDI DLL hashes, the complete .NET runtime, platform exclusions, correct Mac
apphost architectures and the declared native asset hashes/architecture slices.

Empty-cache restore probes accepted the exact 0.3.6 packages, refused a missing
bundle even with an alternate local feed, and refused a synthetic higher version.
All four applications now select the same exact 0.3.6 family with identical
archive bytes and source provenance. Source-development overrides were inactive.

No native MIDI enumeration, device connection or MIDI send was performed.
Native macOS execution and physical interoperability remain untested.
