using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PresetSender.Tests.Support;

namespace PresetSender.Tests;

public sealed class BankSelectionTests
{
    public static IEnumerable<object[]> StartingBanks()
    {
        foreach (int offset in new[] { 0, 1 })
        {
            foreach (int channel in new[] { 1, 6, 16, 0 })
            {
                foreach (int bank in new[] { 0, 1, 2, 3, 4, 7, 127 })
                {
                    yield return new object[] { offset, channel, bank };
                }
            }
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(StartingBanks))]
    public async Task RequestedPresetIsSelectedRegardlessOfStartingBank(int offset, int outputChannel, int startingBank)
    {
        var midi = new CapturedMidiBackend();
        var receiver = new BankedReceiver();
        var window = new SenderWindow(true, null, midi, new SenderSettings { Offset = offset, OutputChannel = outputChannel, AutoSendDigits = 4 });
        window.Show();
        try
        {
            await Connect(window);
            // Exercise every supported preset through the application's entry/send path.
            // Re-seed the receiver before EVERY request: bank selection cannot be cached.
            for (int program = 0; program < 1024; program++)
            {
                receiver.SetStartingBank(startingBank);
                foreach (char digit in (program + offset).ToString(CultureInfo.InvariantCulture)) { window.Execute(digit.ToString()); }
                Assert.Empty(midi.Output.Messages);
                window.Execute("Send");
                CheckSelection(program);
            }

            foreach (var (command, expected) in new[] { ("Next", 0), ("Prev", 1023), ("Last", 0) })
            {
                receiver.SetStartingBank(startingBank);
                window.Execute(command);
                CheckSelection(expected);
            }

            void CheckSelection(int expected)
            {
                foreach (int message in midi.Output.Messages) { receiver.Receive(message); }
                var expectedSelections = Enumerable.Range(1, 16)
                    .Where(channel => outputChannel == 0 || channel == outputChannel)
                    .Select(channel => (Channel: channel, Preset: expected)).ToArray();
                Assert.Equal(expectedSelections, receiver.Selections);
                foreach (var selection in expectedSelections)
                {
                    Assert.Equal(expected + offset, receiver.SelectedPresets[selection.Channel - 1] + offset);
                }
                midi.Output.Messages.Clear();
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task FourDigitKeyboardAndMidiAutoSendSelectTheLastPreset(int offset, bool midiEntry)
    {
        var midi = new CapturedMidiBackend();
        var receiver = new BankedReceiver();
        receiver.SetStartingBank(127);
        var window = new SenderWindow(true, null, midi, new SenderSettings
        {
            InputId = "controller",
            Offset = offset,
            AutoSend = true,
            AutoSendDigits = 4,
            AutoSendDelayMs = 20
        });
        window.Show();
        try
        {
            await Connect(window);
            Find<Button>(window, "SaveConfiguration").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Find<Button>(window, "Digit1").Focus();
            string entry = offset == 0 ? "1023" : "1024";
            foreach (char digit in entry)
            {
                Assert.Empty(midi.Output.Messages);
                if (midiEntry)
                {
                    int note = digit == '0' ? 45 : 35 + digit - '0';
                    midi.Input.Emit(0x90 | (note << 8) | (100 << 16));
                    midi.Input.Emit(0x80 | (note << 8));
                    Dispatcher.UIThread.RunJobs();
                }
                else
                {
                    var key = Enum.Parse<PhysicalKey>("Digit" + digit);
                    window.KeyPressQwerty(key, RawInputModifiers.None);
                    window.KeyReleaseQwerty(key, RawInputModifiers.None);
                }
            }
            await WaitUntil(() => midi.Output.Messages.Count == 2);
            foreach (int message in midi.Output.Messages) { receiver.Receive(message); }
            Assert.Equal(new[] { (Channel: 1, Preset: 1023) }, receiver.Selections);
            Assert.Equal(entry, Find<TextBlock>(window, "ProgramDisplay").Text);
            Assert.Contains(window.OwnedWindows.SelectMany(child => child.GetVisualDescendants().OfType<TextBlock>())
                .Concat(window.GetVisualDescendants().OfType<TextBlock>()), text => text.Text == $"Sent preset {entry}.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NavigationCrossesBankBoundariesAndLastReturnsToTheAbsolutePreset(int offset)
    {
        var midi = new CapturedMidiBackend();
        var receiver = new BankedReceiver();
        var window = new SenderWindow(true, null, midi, new SenderSettings { Offset = offset, AutoSendDigits = 4 });
        window.Show();
        try
        {
            await Connect(window);
            window.Execute("Prev"); Check(1023);
            window.Execute("Next"); Check(0);
            for (int boundary = 128; boundary < 1024; boundary += 128)
            {
                foreach (char digit in (boundary - 1 + offset).ToString(CultureInfo.InvariantCulture)) { window.Execute(digit.ToString()); }
                window.Execute("Send"); Check(boundary - 1);
                window.Execute("Next"); Check(boundary);
                window.Execute("Last"); Check(boundary - 1);
                window.Execute("Last"); Check(boundary);
                window.Execute("Prev"); Check(boundary - 1);
            }

            void Check(int expected)
            {
                receiver.SetStartingBank(7);
                foreach (int message in midi.Output.Messages) { receiver.Receive(message); }
                Assert.Equal(new[] { (Channel: 1, Preset: expected) }, receiver.Selections);
                midi.Output.Messages.Clear();
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FailedBankOrProgramSendDoesNotSelectPresetOrConsumeEntry(int successfulMessages)
    {
        var midi = new CapturedMidiBackend();
        var receiver = new BankedReceiver();
        receiver.SetStartingBank(1);
        midi.Output.FailAfter = successfulMessages;
        var window = new SenderWindow(true, null, midi, new SenderSettings { Offset = 1, AutoSendDigits = 4 });
        window.Show();
        try
        {
            await Connect(window);
            foreach (char digit in "1024") { window.Execute(digit.ToString()); }
            window.Execute("Send");
            foreach (int message in midi.Output.Messages) { receiver.Receive(message); }
            Assert.Empty(receiver.Selections);
            Assert.True(midi.Output.Disposed);
            Assert.Equal("1024", Find<TextBlock>(window, "ProgramDisplay").Text);
        }
        finally { window.Close(); }
    }

    // Independent CC#0 + PC receiver, using the FM9's bank rule with eight banks:
    // https://www.fractalaudio.com/downloads/manuals/FM9/FM9-Owners-Manual.pdf (p. 124).
    // A bank command only changes remembered state; a PC selects bank * 128 + program.
    private sealed class BankedReceiver
    {
        private readonly int[] _banks = new int[16];
        public int[] SelectedPresets { get; } = new int[16];
        public List<(int Channel, int Preset)> Selections { get; } = [];

        public void SetStartingBank(int bank)
        {
            Array.Fill(_banks, bank);
            Array.Fill(SelectedPresets, bank * 128 + 42);
            Selections.Clear();
        }

        public void Receive(int message)
        {
            int channel = message & 15;
            int kind = message & 0xf0;
            int number = (message >> 8) & 127;
            if (kind == 0xb0 && number == 0) { _banks[channel] = (message >> 16) & 127; }
            if (kind == 0xc0)
            {
                SelectedPresets[channel] = _banks[channel] * 128 + number;
                Selections.Add((channel + 1, SelectedPresets[channel]));
            }
        }
    }

    private static async Task Connect(SenderWindow window)
    {
        Find<Button>(window, "OpenConfiguration").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
        Find<Button>(window, "ConnectMidi").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => Find<Button>(window, "DisconnectMidi").IsEnabled);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Concat(window.OwnedWindows.SelectMany(child => child.GetVisualDescendants().OfType<T>()))
            .First(control => control.Name == name);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) { return; }
            await Task.Delay(10);
        }
        Assert.True(condition(), "Timed out waiting for MIDI setup.");
    }
}
