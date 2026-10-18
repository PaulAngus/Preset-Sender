#if SENDER_WINDOWS
using MightBeUseful.Midi;
using MightBeUseful.Midi.Windows;

namespace PresetSender;

// Delegate short-message transport, endpoint topology, SDK and cleanup to the shared provider.
internal sealed class WindowsVirtualMidiOutput : IMidiOutputPort
{
    private readonly IMidiOutputPort _output;
    private readonly Mutex _instance;
    private WindowsVirtualMidiOutput(IMidiOutputPort output, Mutex instance) { _output = output; _instance = instance; }
    public static WindowsVirtualMidiOutput Open()
    {
        string? configuredName = Environment.GetEnvironmentVariable("PRESET_SENDER_VIRTUAL_DEVICE_NAME");
        string name = ResolveName(configuredName, SenderSettings.ConfiguredDirectory);
        string mutexName = configuredName is null ? @"Local\PresetSender.VirtualMidiOutput"
            : @"Local\PresetSender.VirtualMidiOutput." + name;
        var instance = new Mutex(false, mutexName, out bool created);
        if (!created) { instance.Dispose(); throw new InvalidOperationException("Preset Sender is already publishing its MIDI device in another instance."); }
        try { return new(WindowsMidiBackend.OpenVirtualOutput(name), instance); }
        catch { instance.Dispose(); throw; }
    }
    internal static string ResolveName(string? configuredName, string? profile)
    {
        if (configuredName is null) { return "Preset Sender"; }
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile)
            || !configuredName.StartsWith("PresetSender-Test-", StringComparison.Ordinal) || configuredName.Length > 31
            || configuredName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Test virtual names require an isolated absolute data directory and a PresetSender-Test- name containing only ASCII letters, digits and hyphens (at most 31 characters).");
        }
        return configuredName;
    }
    public void Send(int message) => _output.Send(message);
    public void SendBuffer(byte[] frame) => throw new NotSupportedException("Preset Sender does not forward SysEx.");
    public void Dispose() { try { _output.Dispose(); } finally { _instance.Dispose(); } }
}
#endif
