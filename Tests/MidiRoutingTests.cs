using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightBeUseful.Midi;
using FakeMidi = PresetSender.Tests.Support.CapturedMidiBackend;

namespace PresetSender.Tests;

public sealed class MidiRoutingTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectingAfterDeviceListChangesNeverOpensTheReplacementDevice(bool selectedDeviceRemoved)
    {
        var midi = new FakeMidi();
        var window = new SenderWindow(true, null, midi, new SenderSettings { OutputName = "Test receiver" });
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<Button>(window, "RefreshMidi").IsEnabled && midi.RefreshCount > 0);
            // Another device now occupies the old numeric slot. The chosen device
            // either moved to slot 7 or was unplugged entirely.
            midi.Outputs = selectedDeviceRemoved ? [(0, "Unrelated synthesizer")]
                : [(0, "Unrelated synthesizer"), (7, "Test receiver")];
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => Find<Button>(window, "ConnectMidi").IsEnabled || Find<Button>(window, "DisconnectMidi").IsEnabled);
            if (selectedDeviceRemoved)
            {
                Assert.Null(midi.OpenedOutputIndex);
                window.Execute("1"); window.Execute("Send");
                Assert.Empty(midi.Output.Messages);
            }
            else
            {
                Assert.Equal(7, midi.OpenedOutputIndex);
                Assert.True(Find<Button>(window, "DisconnectMidi").IsEnabled);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SlowDetectionLeavesConfigurationResponsiveAndRunsOffUiThread()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var midi = new FakeMidi
        {
            RefreshAction = () =>
            {
                Assert.False(Dispatcher.UIThread.CheckAccess());
                started.TrySetResult();
                release.Wait();
            }
        };
        var window = new SenderWindow(true, null, midi);
        window.Show();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            OpenConfiguration(window);
            Assert.Single(window.OwnedWindows);
            Assert.False(Find<Button>(window, "RefreshMidi").IsEnabled);
            release.Set();
            await WaitUntil(() => Find<Button>(window, "RefreshMidi").IsEnabled);
        }
        finally { release.Set(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task RefreshRecoversVirtualOutputWithoutRestartAndKeepsSavedSelection()
    {
        var midi = new FakeMidi();
        var window = new SenderWindow(true, null, midi, new SenderSettings { VirtualOutput = true });
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<Button>(window, "RefreshMidi").IsEnabled && midi.RefreshCount == 1);
            Assert.Equal(1, Find<ComboBox>(window, "MidiOutput").ItemCount);
            Assert.Null(Find<ComboBox>(window, "MidiOutput").SelectedItem);
            midi.RecoverVirtualOnRefresh = true;
            Click(Find<Button>(window, "RefreshMidi"));
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 2);
            Assert.Equal(2, midi.RefreshCount);
            Assert.Equal("Preset Sender (virtual device)", Find<ComboBox>(window, "MidiOutput").SelectedItem?.ToString());
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => Find<Button>(window, "DisconnectMidi").IsEnabled);
            Assert.True(midi.OpenedVirtual);
            Assert.Empty(midi.Output.Messages);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task VirtualDevicePublishesWithoutSendingAndBroadcastsOnEveryChannel()
    {
        var midi = new FakeMidi { SupportsVirtualOutput = true };
        var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 2);
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => Find<Button>(window, "DisconnectMidi").IsEnabled);
            Assert.True(midi.OpenedVirtual);
            Assert.Empty(midi.Output.Messages);
            Dispatcher.UIThread.RunJobs();
            Find<ComboBox>(window, "OutputChannel").SelectedIndex = 0;
            window.Execute("4"); window.Execute("2"); window.Execute("Send");
            Assert.Equal(Enumerable.Range(0, 16).SelectMany(channel => new[] { 0xb0 | channel, 0xc0 | channel | (42 << 8) }), midi.Output.Messages);
            Assert.Equal("042", Find<TextBlock>(window, "ProgramDisplay").Text);
            Click(Find<Button>(window, "DisconnectMidi"));
            Assert.True(midi.Output.Disposed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ForwardedMidiKeepsOriginalChannelsAndPendingEntry()
    {
        var midi = new FakeMidi { SupportsVirtualOutput = true };
        var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 2);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1;
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => midi.Input.Started);
            Dispatcher.UIThread.RunJobs();
            Find<ComboBox>(window, "OutputChannel").SelectedIndex = 0;
            Find<CheckBox>(window, "ForwardMidi").IsChecked = true;
            var inputChannel = Find<ComboBox>(window, "InputChannel");
            inputChannel.SelectedIndex = 3;
            window.Execute("4"); window.Execute("2");
            midi.Input.Emit(0xc1 | (127 << 8)); Dispatcher.UIThread.RunJobs();
            midi.Input.Emit(0xc2 | (127 << 8)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { 0xc1 | (127 << 8), 0xc2 | (127 << 8) }, midi.Output.Messages);
            Assert.Equal("042", Find<TextBlock>(window, "ProgramDisplay").Text);
            window.Execute("Last"); Assert.Equal(2, midi.Output.Messages.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ForwardingPassesChannelMessagesAndDropsSystemMessagesAndSysEx()
    {
        var midi = new FakeMidi(); var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1;
            Click(Find<Button>(window, "ConnectMidi")); await WaitUntil(() => midi.Input.Started);
            int[] messages = [0x92 | (60 << 8) | (100 << 16), 0xb2, 0xb2 | (32 << 8) | (3 << 16),
                0xa2 | (60 << 8) | (80 << 16), 0xd2 | (90 << 8), 0xe2 | (127 << 8) | (64 << 16), 0xc2 | (42 << 8),
                0x82 | (60 << 8) | (40 << 16), 0x9f | (80 << 8)];
            foreach (int raw in messages) { midi.Input.Emit(raw); }
            Dispatcher.UIThread.RunJobs(); Assert.Empty(midi.Output.Messages);
            Find<CheckBox>(window, "ForwardMidi").IsChecked = true;
            Find<ComboBox>(window, "InputChannel").SelectedIndex = 1;
            foreach (int raw in messages) { midi.Input.Emit(raw); }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(messages, midi.Output.Messages);
            foreach (int raw in new[] { 0xf0, 0xf1, 0xf2, 0xf3, 0xf6, 0xf7, 0xf8, 0xfa, 0xfb, 0xfc, 0xfe, 0xff }) { midi.Input.Emit(raw); }
            midi.Input.EmitSysEx([0xf0, 0x7d, 0x01, 0xf7]); Dispatcher.UIThread.RunJobs(); Assert.Equal(messages, midi.Output.Messages); Assert.False(midi.Output.Disposed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EntryAndLearnedControlsConsumePressesRepeatsReleasesAndPressure()
    {
        var midi = new FakeMidi();
        var settings = new SenderSettings { ForwardMidi = true, Bindings = [new(0x90, 1, 60, "4"), new(0xb0, 1, 20, "Clear"), new(0xc0, 1, 70, "Send")] };
        var window = new SenderWindow(true, null, midi, settings); window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1; Click(Find<Button>(window, "ConnectMidi")); await WaitUntil(() => midi.Input.Started);
            Emit(0x90 | (36 << 8) | (100 << 16)); Emit(0x90 | (36 << 8) | (100 << 16));
            Assert.Equal("011", Find<TextBlock>(window, "ProgramDisplay").Text);
            Emit(0xa0 | (36 << 8) | (80 << 16)); Emit(0x80 | (36 << 8)); Emit(0x90 | (36 << 8));
            window.Execute("Clear"); Emit(0x90 | (60 << 8) | (100 << 16)); Emit(0x90 | (60 << 8) | (100 << 16));
            Assert.Equal("004", Find<TextBlock>(window, "ProgramDisplay").Text);
            Emit(0xa0 | (60 << 8) | (80 << 16)); Emit(0x80 | (60 << 8));
            Emit(0xb0 | (20 << 8) | (127 << 16)); Emit(0xb0 | (20 << 8) | (64 << 16)); Emit(0xb0 | (20 << 8));
            Assert.Empty(midi.Output.Messages);
            window.Execute("4"); window.Execute("2"); Emit(0xc0 | (70 << 8)); Assert.Equal(new[] { 0xb0, 0xc0 | (42 << 8) }, midi.Output.Messages);
            Click(Find<Button>(window, "ConfigureNoteMapping")); Find<ComboBox>(window, "LearnCommand").SelectedIndex = 2; Click(Find<Button>(window, "LearnMidi"));
            Emit(0xb0 | (22 << 8) | (127 << 16)); Emit(0xb0 | (22 << 8) | (64 << 16));
            Click(Find<Button>(window, "RemoveLearnedMapping"));
            Emit(0xb0 | (22 << 8) | (80 << 16)); Emit(0xb0 | (22 << 8)); Assert.Equal(2, midi.Output.Messages.Count);
            Emit(0xb0 | (22 << 8) | (127 << 16)); Assert.Equal(0xb0 | (22 << 8) | (127 << 16), midi.Output.Messages[^1]);
            Click(Find<Button>(window, "CancelMapping"));
            Emit(0x90 | (37 << 8) | (100 << 16)); Find<CheckBox>(window, "MidiNoteEntry").IsChecked = false;
            Emit(0x80 | (37 << 8)); Assert.Equal(3, midi.Output.Messages.Count);

            void Emit(int raw) { midi.Input.Emit(raw); Dispatcher.UIThread.RunJobs(); }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ForwardedNotesAreReleasedWhenRoutingChanges()
    {
        var midi = new FakeMidi(); var window = new SenderWindow(true, null, midi, new SenderSettings { ForwardMidi = true });
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1; Click(Find<Button>(window, "ConnectMidi")); await WaitUntil(() => midi.Input.Started);
            int on = 0x94 | (60 << 8) | (100 << 16), off = 0x84 | (60 << 8);
            midi.Input.Emit(on); Dispatcher.UIThread.RunJobs(); Find<CheckBox>(window, "ForwardMidi").IsChecked = false;
            Assert.Equal(new[] { on, off }, midi.Output.Messages);
            Find<CheckBox>(window, "ForwardMidi").IsChecked = true; midi.Input.Emit(on); Dispatcher.UIThread.RunJobs();
            Click(Find<Button>(window, "ConfigureNoteMapping")); Click(Find<Button>(window, "LearnMidi"));
            midi.Input.Emit(on); Dispatcher.UIThread.RunJobs(); Assert.Equal(new[] { on, off, on, off }, midi.Output.Messages);
            midi.Input.Emit(off); Dispatcher.UIThread.RunJobs(); Assert.Equal(4, midi.Output.Messages.Count);
            Click(Find<Button>(window, "CancelMapping"));
            int second = 0x9f | (80 << 8) | (100 << 16); midi.Input.Emit(second); Dispatcher.UIThread.RunJobs();
            Click(Find<Button>(window, "ConfigureNoteMapping")); Find<TextBox>(window, "MappingNoteNumber").Text = "80"; Click(Find<Button>(window, "SaveMapping"));
            midi.Input.Emit(0x8f | (80 << 8)); Dispatcher.UIThread.RunJobs(); Assert.Equal(0x8f | (80 << 8), midi.Output.Messages[^1]);
            midi.Input.Emit(0x9f | (81 << 8) | (100 << 16)); Dispatcher.UIThread.RunJobs();
            Click(Find<Button>(window, "DisconnectMidi")); Assert.Equal(0x8f | (81 << 8), midi.Output.Messages[^1]); Assert.True(midi.Output.Disposed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ForwardingFailureDisconnectsAndRetainsPendingEntry()
    {
        var midi = new FakeMidi { FailSend = true }; var window = new SenderWindow(true, null, midi, new SenderSettings { ForwardMidi = true });
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1; Click(Find<Button>(window, "ConnectMidi")); await WaitUntil(() => midi.Input.Started);
            window.Execute("4"); window.Execute("2"); midi.Input.Emit(0xe0 | (64 << 16)); Dispatcher.UIThread.RunJobs();
            Assert.True(midi.Output.Disposed); Assert.True(midi.Input.Disposed); Assert.Equal("042", Find<TextBlock>(window, "ProgramDisplay").Text);
            Assert.Contains(Controls<TextBlock>(window), label => label.Text?.StartsWith("MIDI forwarding failed:", StringComparison.Ordinal) == true);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PartialBroadcastFailureKeepsEntryAndClosesVirtualDevice()
    {
        var midi = new FakeMidi { SupportsVirtualOutput = true };
        midi.Output.FailAfter = 5;
        var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 2);
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => Find<Button>(window, "DisconnectMidi").IsEnabled);
            Dispatcher.UIThread.RunJobs();
            Find<ComboBox>(window, "OutputChannel").SelectedIndex = 0;
            window.Execute("4"); window.Execute("2"); window.Execute("Send");
            Assert.Equal(5, midi.Output.Messages.Count);
            Assert.True(midi.Output.Disposed);
            Assert.Equal("042", Find<TextBlock>(window, "ProgramDisplay").Text);
            Assert.Contains(Controls<TextBlock>(window), c => c.Text?.Contains("Some channels may already", StringComparison.Ordinal) == true);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LearnedProgramButtonsEnterTwoDigitsThenSendOnChosenChannel()
    {
        var midi = new FakeMidi();
        var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1;
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => midi.Input.Started);
            Dispatcher.UIThread.RunJobs();
            Find<ComboBox>(window, "OutputChannel").SelectedIndex = 16;
            Click(Find<Button>(window, "ConfigureNoteMapping")); Dispatcher.UIThread.RunJobs();
            var command = Find<ComboBox>(window, "LearnCommand");
            Learn(4, 10); Learn(2, 11); Learn(10, 12);
            Assert.Empty(midi.Output.Messages);
            Click(Find<Button>(window, "CancelMapping")); Dispatcher.UIThread.RunJobs();
            midi.Input.Emit(0xc0 | (10 << 8)); Dispatcher.UIThread.RunJobs();
            midi.Input.Emit(0xc0 | (11 << 8)); Dispatcher.UIThread.RunJobs();
            Assert.Empty(midi.Output.Messages);
            Assert.Equal("042", Find<TextBlock>(window, "ProgramDisplay").Text);
            midi.Input.Emit(0xc0 | (12 << 8)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { 0xbf, 0xcf | (42 << 8) }, midi.Output.Messages);
            Click(Find<Button>(window, "DisconnectMidi"));
            Assert.True(midi.Input.Disposed); Assert.True(midi.Output.Disposed);
            midi.Input.Emit(0xc0 | (12 << 8)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, midi.Output.Messages.Count);

            void Learn(int index, int pc)
            {
                command.SelectedIndex = index;
                Click(Find<Button>(window, "LearnMidi"));
                midi.Input.Emit(0xc0 | (pc << 8)); Dispatcher.UIThread.RunJobs();
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedInputStartupReleasesBothPortsAndKeepsEntry()
    {
        var midi = new FakeMidi { FailStart = true };
        var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1;
            window.Execute("4"); window.Execute("2");
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => midi.Output.Disposed);
            Assert.True(midi.Input.Disposed);
            Assert.Empty(midi.Output.Messages);
            Assert.Equal("042", Find<TextBlock>(window, "ProgramDisplay").Text);
            Assert.True(Find<Button>(window, "SendProgram").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedSendDisconnectsWithoutClaimingSuccessOrLosingPendingNumber()
    {
        var midi = new FakeMidi { FailSend = true };
        var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Click(Find<Button>(window, "ConnectMidi"));
            await WaitUntil(() => Find<Button>(window, "DisconnectMidi").IsEnabled);
            window.Execute("4"); window.Execute("2"); window.Execute("Send");
            Assert.True(midi.Output.Disposed); Assert.Empty(midi.Output.Messages);
            Assert.Equal("042", Find<TextBlock>(window, "ProgramDisplay").Text);
            Assert.True(Find<Button>(window, "SendProgram").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingConfigurationKeepsMidiConnectedAndKeyboardEntryWorks()
    {
        var midi = new FakeMidi(); var window = new SenderWindow(true, null, midi);
        window.Show(); OpenConfiguration(window);
        try
        {
            await WaitUntil(() => Find<ComboBox>(window, "MidiOutput").ItemCount == 1);
            Find<ComboBox>(window, "MidiInput").SelectedIndex = 1;
            Click(Find<Button>(window, "ConnectMidi")); await WaitUntil(() => midi.Input.Started);
            Click(Find<Button>(window, "SaveConfiguration")); Dispatcher.UIThread.RunJobs();
            Assert.True(Find<Button>(window, "OpenConfiguration").IsFocused);
            Assert.True(midi.Input.Started); Assert.False(midi.Output.Disposed);
            foreach (var key in new[] { Avalonia.Input.PhysicalKey.Digit4, Avalonia.Input.PhysicalKey.Digit2, Avalonia.Input.PhysicalKey.Enter })
            {
                window.KeyPressQwerty(key, Avalonia.Input.RawInputModifiers.None); window.KeyReleaseQwerty(key, Avalonia.Input.RawInputModifiers.None);
            }
            Assert.Equal(new[] { 0xb0, 0xc0 | (42 << 8) }, midi.Output.Messages);
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Controls<T>(Window window) where T : Control => window.GetVisualDescendants().OfType<T>().Concat(window.OwnedWindows.SelectMany(Controls<T>));
    private static T Find<T>(Window window, string name) where T : Control => Controls<T>(window).First(c => c.Name == name);
    private static void OpenConfiguration(Window window) { Click(Find<Button>(window, "OpenConfiguration")); Dispatcher.UIThread.RunJobs(); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
