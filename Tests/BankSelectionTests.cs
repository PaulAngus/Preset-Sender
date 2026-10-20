using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
                foreach (int bank in new[] { 0, 1, 2, 3, 127 })
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
        var window = new SenderWindow(true, null, midi, new SenderSettings { Offset = offset, OutputChannel = outputChannel });
        window.Show();
        try
        {
            await Connect(window);
            // Exercise every supported preset through the application's entry/send path.
            // Re-seed the receiver before EVERY request: bank selection cannot be cached.
            for (int program = 0; program < 128; program++)
            {
                receiver.SetStartingBank(startingBank);
                foreach (char digit in (program + offset).ToString(CultureInfo.InvariantCulture)) { window.Execute(digit.ToString()); }
                Assert.Empty(midi.Output.Messages);
                window.Execute("Send");
                CheckSelection(program);
            }

            foreach (var (command, expected) in new[] { ("Next", 0), ("Prev", 127), ("Last", 0) })
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
    [InlineData(0)]
    [InlineData(1)]
    public async Task FailedBankOrProgramSendDoesNotSelectPresetOrConsumeEntry(int successfulMessages)
    {
        var midi = new CapturedMidiBackend();
        var receiver = new BankedReceiver();
        receiver.SetStartingBank(1);
        midi.Output.FailAfter = successfulMessages;
        var window = new SenderWindow(true, null, midi);
        window.Show();
        try
        {
            await Connect(window);
            window.Execute("1"); window.Execute("Send");
            foreach (int message in midi.Output.Messages) { receiver.Receive(message); }
            Assert.Empty(receiver.Selections);
            Assert.True(midi.Output.Disposed);
            Assert.Equal("001", Find<TextBlock>(window, "ProgramDisplay").Text);
        }
        finally { window.Close(); }
    }

    // Independent receiver semantics, following the FM9's CC#0 + PC preset table:
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
