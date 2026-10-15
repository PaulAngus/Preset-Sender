#if SENDER_WINDOWS
using MightBeUseful.Midi;
using MightBeUseful.Midi.Windows;

namespace PresetSender;

// Sender's PC-only product boundary delegates endpoint topology, SDK and cleanup to the shared provider.
internal sealed class WindowsVirtualMidiOutput : IMidiOutputPort
{
    private readonly IMidiOutputPort _output;
    private readonly Mutex _instance;
    private WindowsVirtualMidiOutput(IMidiOutputPort output, Mutex instance) { _output = output; _instance = instance; }
    public static WindowsVirtualMidiOutput Open()
    {
        var instance = new Mutex(false, @"Local\PresetSender.VirtualMidiOutput", out bool created);
        if (!created) { instance.Dispose(); throw new InvalidOperationException("Preset Sender is already publishing its MIDI device in another instance."); }
        try { return new(WindowsMidiBackend.OpenVirtualOutput("Preset Sender"), instance); }
        catch { instance.Dispose(); throw; }
    }
    public void Send(int message)
    {
        if ((message & 0xf0) != 0xc0) { throw new ArgumentException("Only Program Change messages are supported.", nameof(message)); }
        _output.Send(message);
    }
    public void SendBuffer(byte[] frame) => throw new NotSupportedException("Preset Sender sends only Program Change messages.");
    public void Dispose() { try { _output.Dispose(); } finally { _instance.Dispose(); } }
}
#endif
