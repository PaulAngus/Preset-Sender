using MightBeUseful.Midi;
using PresetSender.Platforms;

namespace PresetSender;

internal enum LoopMidiStatus { NotApplicable, Missing, Installed, Unknown }

internal interface ISenderMidiBackend
{
    void RefreshCapabilities() { }
    IReadOnlyList<(string Id, string Name)> GetInputDevices();
    IReadOnlyList<(int Index, string Name)> GetOutputDevices();
    IMidiInputPort OpenInput(string id);
    IMidiOutputPort OpenOutput(int index);
    bool SupportsVirtualOutput { get; }
    IMidiOutputPort OpenVirtualOutput();
    LoopMidiStatus GetLoopMidiStatus() => LoopMidiStatus.NotApplicable;
}

#if TEST_LAB_OFFLINE_WINDOWS
// Compiled only into the audited offline test build. Never touches a native backend.
internal sealed class NativeMidiBackend : ISenderMidiBackend
{
    public void RefreshCapabilities() { }
    public IReadOnlyList<(string Id, string Name)> GetInputDevices() => [];
    public IReadOnlyList<(int Index, string Name)> GetOutputDevices() => [];
    public bool SupportsVirtualOutput => false;
    public IMidiInputPort OpenInput(string id) => throw Disabled();
    public IMidiOutputPort OpenOutput(int index) => throw Disabled();
    public IMidiOutputPort OpenVirtualOutput() => throw Disabled();
    private static InvalidOperationException Disabled() => new("The offline test profile prohibits native MIDI access.");
}
#else
internal sealed class NativeMidiBackend : ISenderMidiBackend
{
#if SENDER_WINDOWS
    public LoopMidiStatus GetLoopMidiStatus() => LoopMidiInstallation.Detect();
#endif
    public void RefreshCapabilities()
    {
#if SENDER_WINDOWS
        var capabilities = MightBeUseful.Midi.Windows.WindowsMidiBackend.RefreshCapabilities();
        try
        {
            string directory = string.IsNullOrWhiteSpace(SenderSettings.ConfiguredDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PresetSender")
                : SenderSettings.ResolveDirectory(SenderSettings.ConfiguredDirectory);
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "midi-backend.log"),
            [
                $"Version: {typeof(NativeMidiBackend).Assembly.GetName().Version}",
                $"Windows: {Environment.OSVersion.Version}",
                $"Backend: {capabilities.Description}",
                $"MIDI API directory: {MightBeUseful.Midi.Windows.WindowsMidiBackend.ApiDirectory}",
                $"Bundled API extraction: {BundledMidiApi.PreparationError ?? "OK"}",
                $"MBU_MIDI_BACKEND: {Environment.GetEnvironmentVariable("MBU_MIDI_BACKEND") ?? "(unset)"}",
                $"MIDI_FORWARDER_MIDI_API: {Environment.GetEnvironmentVariable("MIDI_FORWARDER_MIDI_API") ?? "(unset)"}",
                $"MBU_MIDI_API_DIRECTORY: {Environment.GetEnvironmentVariable("MBU_MIDI_API_DIRECTORY") ?? "(unset)"}"
            ]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
#endif
    }
    public IReadOnlyList<(string Id, string Name)> GetInputDevices() => MidiBackend.GetInputDevices();
    public IReadOnlyList<(int Index, string Name)> GetOutputDevices() => MidiBackend.GetOutputDevices();
    public IMidiInputPort OpenInput(string id) => MidiBackend.OpenInput(id);
    public IMidiOutputPort OpenOutput(int index) => MidiBackend.OpenOutput(index);
#if SENDER_WINDOWS
    public bool SupportsVirtualOutput => MightBeUseful.Midi.Windows.WindowsMidiBackend.Capabilities.CanCreateVirtualPorts;
    public IMidiOutputPort OpenVirtualOutput()
    {
        if (!SupportsVirtualOutput)
        {
            throw new PlatformNotSupportedException("Use an existing hardware or loopback MIDI output on this version of Windows.");
        }
        return VirtualMidiHost.Open();
    }
#else
    public bool SupportsVirtualOutput => false;
    public IMidiOutputPort OpenVirtualOutput() => throw new PlatformNotSupportedException("Built-in virtual output currently requires Windows MIDI Services.");
#endif
}

#endif
