# Pinned shared MIDI dependency

These first-party packages come from the independently buildable [MIDIHelperLibrary repository](https://github.com/PaulAngus/MIDIHelperLibrary). The package and namespace names remain `MightBeUseful.Midi`. MidiVersion.props, version.txt and packages.sha256 identify the selected release. Normal builds restore from this relative feed and need no sibling checkout.

To update all consumers, increment the library version and run its Publish-SharedMidi.ps1 -ConsumerRoot command with the three checkout paths. The script updates packages, version metadata and hashes. Do not copy MIDI implementation source into the applications.

MBU_MIDI_BACKEND=auto/current/legacy controls the shared Windows policy. Preset Sender 0.1.3 bundles its own native API and configures the shared provider's app-local directory on Windows 11 25H2+: users copy only the EXE. Windows 10 uses hardware or user-installed loopMIDI. The shared library still supports optional setup for other consumers via ../../Initialize-SharedMidi.ps1 -EnableWindowsServices. Preset Sender's Initialize-MidiSdk.ps1 prepares the verified build feed without installing runtime components. Application --midi-backend and --list-ports commands report the same provider policy; MBU_MIDI_API_DIRECTORY remains an explicit diagnostic override.
