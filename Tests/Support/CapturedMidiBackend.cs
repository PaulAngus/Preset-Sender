using MightBeUseful.Midi;

namespace PresetSender.Tests.Support;

// Shared fake for complete UI-to-port scenarios. Expected wire bytes belong in tests.
internal sealed class CapturedMidiBackend : ISenderMidiBackend
{
    public int RefreshCount { get; private set; }
    public bool RecoverVirtualOnRefresh { get; set; }
    public Action? RefreshAction { get; set; }
    public CapturedMidiInput Input { get; } = new();
    public CapturedMidiOutput Output { get; } = new();
    public bool FailStart { get => Input.FailStart; set => Input.FailStart = value; }
    public bool FailSend { get => Output.FailSend; set => Output.FailSend = value; }
    public bool SupportsVirtualOutput { get; set; }
    public bool OpenedVirtual { get; private set; }
    public IReadOnlyList<(int Index, string Name)> Outputs { get; set; } = [(0, "Test receiver")];
    public int? OpenedOutputIndex { get; private set; }
    public void RefreshCapabilities()
    {
        RefreshAction?.Invoke();
        RefreshCount++;
        if (RecoverVirtualOnRefresh) { SupportsVirtualOutput = true; }
    }
    public IReadOnlyList<(string Id, string Name)> GetInputDevices() => [("controller", "Test controller")];
    public IReadOnlyList<(int Index, string Name)> GetOutputDevices() => Outputs;
    public IMidiInputPort OpenInput(string id) => Input;
    public IMidiOutputPort OpenOutput(int index) { OpenedOutputIndex = index; return Output; }
    public IMidiOutputPort OpenVirtualOutput() { OpenedVirtual = true; return Output; }
}

internal sealed class CapturedMidiInput : IMidiInputPort
{
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public bool FailStart { get; set; }
    public event EventHandler<MidiShortMessageEventArgs>? MessageReceived;
    public event EventHandler<MidiSysExMessageEventArgs>? SysexMessageReceived;
    public event EventHandler<string>? ErrorReceived;
    public void Emit(int raw) => MessageReceived?.Invoke(this, new(raw));
    public void EmitSysEx(byte[] bytes) => SysexMessageReceived?.Invoke(this, new(bytes));
    public void Fail(string message) => ErrorReceived?.Invoke(this, message);
    public void Start()
    {
        if (FailStart) { throw new IOException("Simulated startup failure"); }
        Started = true;
    }
    public void Stop() => Started = false;
    public void Dispose() => Disposed = true;
}

internal sealed class CapturedMidiOutput : IMidiOutputPort
{
    public List<int> Messages { get; } = [];
    public bool FailSend { get; set; }
    public int FailAfter { get; set; } = int.MaxValue;
    public bool Disposed { get; private set; }
    public void Send(int message)
    {
        if (FailSend || Messages.Count >= FailAfter) { throw new IOException("Simulated output failure"); }
        Messages.Add(message);
    }
    public void SendBuffer(byte[] frame) => throw new InvalidOperationException("Generic sender must not send SysEx.");
    public void Dispose() => Disposed = true;
}
