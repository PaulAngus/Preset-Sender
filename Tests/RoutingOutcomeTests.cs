using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PresetSender.Tests.Support;

namespace PresetSender.Tests;

public sealed class RoutingOutcomeTests
{
    [AvaloniaTheory]
    [InlineData(false, "disable")]
    [InlineData(true, "remove")]
    [InlineData(false, "disconnect")]
    [InlineData(true, "disconnect")]
    [InlineData(false, "close")]
    [InlineData(true, "close")]
    public async Task StoppingRouteSilencesHeldAndSustainedNotesWithoutTouchingUnrelatedChannels(bool thru, string stop)
    {
        var midi = new OutcomeBackend();
        var window = MakeWindow(midi);
        window.Show();
        try
        {
            await Connect(window);
            var input = thru ? midi.Thru : midi.Input;
            // An unrelated sound was established outside this application's route.
            midi.Receiver.Receive(Cc(10, 64, 127)); midi.Receiver.Receive(Note(10, 70, true));
            PlaySustainedAndHeldNotes(input);
            Assert.True(midi.Receiver.IsSounding(3, 60)); Assert.True(midi.Receiver.IsSounding(3, 61));
            if (stop == "disable") { Find<CheckBox>(window, "ForwardMidi").IsChecked = false; }
            else if (stop == "remove")
            {
                Controls<CheckBox>(window).Single(c => c.Name == "ThruInput" && Equals(c.Content, "Thru controller")).IsChecked = false;
            }
            else if (stop == "close") { window.Close(); }
            else { Click(Find<Button>(window, "DisconnectMidi")); }
            Pump();
            Assert.False(midi.Receiver.IsSounding(3, 60)); Assert.False(midi.Receiver.IsSounding(3, 61));
            Assert.False(midi.Receiver.Sustain[2]);
            Assert.True(midi.Receiver.IsSounding(10, 70)); Assert.True(midi.Receiver.Sustain[9]);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task EchoLoopStopsAndReceiverFallsSilent(bool thru, bool generatedPreset, bool allChannels)
    {
        var midi = new OutcomeBackend();
        var window = MakeWindow(midi);
        window.Show();
        try
        {
            await Connect(window);
            if (allChannels) { Find<ComboBox>(window, "OutputChannel").SelectedIndex = 0; }
            PlaySustainedAndHeldNotes(thru ? midi.Thru : midi.Input);
            midi.Echo = thru ? midi.Thru : midi.Input;
            if (generatedPreset) { window.Execute("1"); window.Execute("Send"); }
            else { midi.Echo.Emit(0xc2 | (1 << 8)); }
            await Until(() => midi.Output.Disposed);
            Assert.False(midi.Overflow, "Feedback ran until the test receiver's emergency limit.");
            Assert.InRange(midi.Receiver.ProgramSelections, 1, 400);
            Assert.False(midi.Receiver.IsSounding(3, 60)); Assert.False(midi.Receiver.IsSounding(3, 61));
            Assert.False(midi.Receiver.Sustain[2]);
            Assert.Contains(Controls<TextBlock>(window), c => c.Text?.Contains("feedback", StringComparison.OrdinalIgnoreCase) == true);
            int count = midi.Output.Messages.Count;
            midi.Echo.Emit(0xc2 | (1 << 8)); Pump();
            Assert.Equal(count, midi.Output.Messages.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DisablingOneRouteDoesNotReleaseAnotherRoutesSustainOnTheSameChannel()
    {
        var midi = new OutcomeBackend(); var window = MakeWindow(midi); window.Show();
        try
        {
            await Connect(window);
            PlaySustainedAndHeldNotes(midi.Input);
            midi.Thru.Emit(Cc(3, 64, 127)); midi.Thru.Emit(Note(3, 65, true)); midi.Thru.Emit(Note(3, 65, false)); Pump();
            Find<CheckBox>(window, "ForwardMidi").IsChecked = false; Pump();
            Assert.True(midi.Receiver.Sustain[2]); Assert.True(midi.Receiver.IsSounding(3, 65));
            midi.Thru.Emit(Cc(3, 64, 0)); Pump();
            Assert.False(midi.Receiver.IsSounding(3, 60)); Assert.False(midi.Receiver.IsSounding(3, 61)); Assert.False(midi.Receiver.IsSounding(3, 65));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OrdinaryRepeatedMessagesAndPedalReleaseStillReachReceiver()
    {
        var midi = new OutcomeBackend(); var window = MakeWindow(midi); window.Show();
        try
        {
            await Connect(window);
            for (int i = 0; i < 32; i++) { midi.Input.Emit(0xc2 | (1 << 8)); Pump(); }
            Assert.Equal(32, midi.Receiver.ProgramSelections); Assert.False(midi.Output.Disposed);
            PlaySustainedAndHeldNotes(midi.Input);
            midi.Input.Emit(Cc(3, 64, 63)); Pump();
            Assert.False(midi.Receiver.IsSounding(3, 60));
            Assert.True(midi.Receiver.IsSounding(3, 61));
            midi.Input.Emit(Note(3, 61, false)); Pump();
            Assert.False(midi.Receiver.IsSounding(3, 61));
        }
        finally { window.Close(); }
    }

    private static SenderWindow MakeWindow(OutcomeBackend midi) => new(true, null, midi,
        new SenderSettings { InputId = "main", ForwardMidi = true, MidiEntryEnabled = false, ThruInputs = [new("thru", "Thru controller")] });

    private static void PlaySustainedAndHeldNotes(CapturedMidiInput input)
    {
        input.Emit(Cc(3, 64, 127)); input.Emit(Note(3, 60, true)); input.Emit(Note(3, 60, false));
        input.Emit(Note(3, 61, true)); Pump();
    }

    private static int Cc(int channel, int control, int value) => 0xb0 | (channel - 1) | (control << 8) | (value << 16);
    private static int Note(int channel, int note, bool on) => (on ? 0x90 : 0x80) | (channel - 1) | (note << 8) | (on ? 100 << 16 : 0);

    // Independent synth model: releasing a key while the pedal is down leaves it
    // sounding; releasing the pedal silences only keys that are no longer held.
    private sealed class Receiver
    {
        public bool[] Sustain { get; } = new bool[16];
        private readonly HashSet<(int Channel, int Note)> _held = [];
        private readonly HashSet<(int Channel, int Note)> _sounding = [];
        public int ProgramSelections { get; private set; }
        public bool IsSounding(int channel, int note) => _sounding.Contains((channel, note));
        public void Receive(int raw)
        {
            int kind = raw & 0xf0, channel = (raw & 15) + 1, number = (raw >> 8) & 127, value = (raw >> 16) & 127;
            var key = (channel, number);
            if (kind == 0x90 && value > 0) { _held.Add(key); _sounding.Add(key); }
            else if (kind is 0x80 or 0x90) { _held.Remove(key); if (!Sustain[channel - 1]) { _sounding.Remove(key); } }
            else if (kind == 0xb0 && number == 64)
            {
                Sustain[channel - 1] = value >= 64;
                if (value < 64) { _sounding.RemoveWhere(note => note.Channel == channel && !_held.Contains(note)); }
            }
            else if (kind == 0xc0) { ProgramSelections++; }
        }
    }

    private sealed class OutcomeBackend : ISenderMidiBackend
    {
        public CapturedMidiInput Input { get; } = new();
        public CapturedMidiInput Thru { get; } = new();
        public CapturedMidiOutput Output { get; } = new();
        public Receiver Receiver { get; } = new();
        public CapturedMidiInput? Echo { get; set; }
        public bool Overflow { get; private set; }
        public OutcomeBackend()
        {
            Output.OnSend = raw =>
            {
                Receiver.Receive(raw);
                if (Echo is null) { return; }
                if (Output.Messages.Count > 512) { Overflow = true; throw new IOException("Test emergency limit"); }
                Echo.Emit(raw);
            };
        }
        public bool SupportsVirtualOutput => false;
        public IReadOnlyList<(string Id, string Name)> GetInputDevices() => [("main", "Main controller"), ("thru", "Thru controller")];
        public IReadOnlyList<(int Index, string Name)> GetOutputDevices() => [(0, "Receiver")];
        public IMidiInputPort OpenInput(string id) => id == "main" ? Input : Thru;
        public IMidiOutputPort OpenOutput(int index) => Output;
        public IMidiOutputPort OpenVirtualOutput() => throw new NotSupportedException();
    }

    private static async Task Connect(SenderWindow window)
    {
        Click(Find<Button>(window, "OpenConfiguration"));
        await Until(() => Find<Button>(window, "RefreshMidi").IsEnabled);
        Click(Find<Button>(window, "ConnectMidi"));
        await Until(() => Find<Button>(window, "DisconnectMidi").IsEnabled);
    }
    private static IEnumerable<T> Controls<T>(Window window) where T : Control => window.GetVisualDescendants().OfType<T>().Concat(window.OwnedWindows.SelectMany(Controls<T>));
    private static T Find<T>(Window window, string name) where T : Control => Controls<T>(window).First(c => c.Name == name);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { Pump(); if (condition()) { return; } await Task.Delay(10); }
        Assert.True(condition(), "Timed out waiting for MIDI state.");
    }
}
