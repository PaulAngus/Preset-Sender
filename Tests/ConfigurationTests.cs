using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MightBeUseful.Midi;

namespace PresetSender.Tests;

public sealed class ConfigurationTests
{
    [AvaloniaTheory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    public void PresetDisplayPadsEntrySentPresetsAndNavigation(int digits, int offset)
    {
        var sent = new List<int>(); var window = new SenderWindow(true, sent.Add, settings: new SenderSettings { AutoSendDigits = digits, Offset = offset }) { Width = offset == 0 ? 460 : 360, RequestedThemeVariant = offset == 0 ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show(); Pump();
        try
        {
            Assert.Equal(new string('-', digits), Display(window));
            string seven = "7".PadLeft(digits, '0'), eight = "8".PadLeft(digits, '0');
            window.Execute("7"); Assert.Equal(seven, Display(window)); window.Execute("Send"); Assert.Equal(seven, Display(window));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == $"Sent preset {seven}.");
            Capture(window, $"preset-sent-{digits}-{offset}");
            Assert.Equal(new[] { 0xb0, 0xc0 | ((7 - offset) << 8) }, sent);
            window.Execute("Next"); Assert.Equal(eight, Display(window)); window.Execute("Last"); Assert.Equal(seven, Display(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConfigurationCancelRestoresDraftAndSavePersistsImmediatelyWithRetry()
    {
        var settings = new SenderSettings(); var saved = new List<SenderSettings>(); bool fail = false;
        var midi = new TestMidi(); var window = new SenderWindow(true, null, midi, settings, value => { if (fail) { throw new IOException("disk full"); } saved.Add(value.Clone()); });
        window.Show(); OpenConfiguration(window);
        try
        {
            await Ready(window); var config = window.OwnedWindows.Single();
            Find<ComboBox>(config, "MidiInput").SelectedIndex = 1; Click(Find<Button>(config, "ConnectMidi")); await Until(() => midi.Inputs["main"].Started);
            Find<NumericUpDown>(config, "AutoSendDigits").Value = 4; Find<ComboBox>(config, "OutputChannel").SelectedIndex = 6;
            Find<CheckBox>(config, "AutoSend").IsChecked = true;
            Controls<CheckBox>(config).Single(c => c.Name == "ThruInput" && Equals(c.Content, "FM9")).IsChecked = true;
            Assert.True(midi.Inputs["fm9"].Started);
            Click(Find<Button>(config, "ConfigureNoteMapping")); var map = config.OwnedWindows.Single();
            Find<TextBox>(map, "MappingNoteNumber").Text = "60"; Find<ComboBox>(map, "LearnCommand").SelectedIndex = 4; Click(Find<Button>(map, "LearnMidi"));
            midi.Inputs["main"].Emit(0xb0 | (20 << 8) | (127 << 16)); Pump(); Click(Find<Button>(map, "SaveMapping"));
            Assert.Empty(saved); Assert.Equal("----", Display(window));
            Click(Find<Button>(config, "CancelConfiguration")); Assert.Empty(saved); Assert.Equal(3, settings.AutoSendDigits); Assert.False(settings.AutoSend);
            Assert.Contains(36, settings.MidiNoteMap.Keys); Assert.DoesNotContain(60, settings.MidiNoteMap.Keys); Assert.Empty(settings.Bindings); Assert.Empty(settings.ThruInputs);
            Assert.True(midi.Inputs["fm9"].Disposed); Assert.True(midi.Inputs["main"].Started); Assert.False(midi.Output.Disposed); Assert.Equal("---", Display(window));
            OpenConfiguration(window); config = window.OwnedWindows.Single();
            Assert.Equal(3, Find<NumericUpDown>(config, "AutoSendDigits").Value); Assert.Equal(1, Find<ComboBox>(config, "OutputChannel").SelectedIndex);
            Find<NumericUpDown>(config, "AutoSendDigits").Value = 4; Find<ComboBox>(config, "OutputChannel").SelectedIndex = 6;
            fail = true; Click(Find<Button>(config, "SaveConfiguration")); Assert.True(config.IsVisible); Assert.Empty(saved);
            Assert.Contains(config.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains("disk full", StringComparison.Ordinal) == true);
            fail = false; Click(Find<Button>(config, "SaveConfiguration")); Assert.False(config.IsVisible); Assert.Single(saved);
            Assert.Equal(4, saved[0].AutoSendDigits); Assert.Equal(6, saved[0].OutputChannel); Assert.True(Find<Button>(window, "OpenConfiguration").IsFocused);
            Assert.True(midi.Inputs["main"].Started); Assert.False(midi.Output.Disposed);
        }
        finally { fail = false; window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelDuringConnectRestoresPreferencesAndKeepsTheRequestedConnection()
    {
        using var release = new ManualResetEventSlim(); var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var midi = new TestMidi { OpeningOutput = () => { opening.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); } } };
        var settings = new SenderSettings(); var window = new SenderWindow(true, null, midi, settings);
        window.Show(); OpenConfiguration(window);
        try
        {
            await Ready(window); var config = window.OwnedWindows.Single(); Find<ComboBox>(config, "MidiInput").SelectedIndex = 1;
            Find<NumericUpDown>(config, "AutoSendDigits").Value = 4; Click(Find<Button>(config, "ConnectMidi")); await opening.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Click(Find<Button>(config, "CancelConfiguration")); Assert.Equal(3, settings.AutoSendDigits);
            release.Set(); await Until(() => midi.Inputs["main"].Started); Assert.False(midi.Output.Disposed);
            OpenConfiguration(window); config = window.OwnedWindows.Single(); Assert.Equal(1, Find<ComboBox>(config, "MidiInput").SelectedIndex);
            Assert.Equal(3, Find<NumericUpDown>(config, "AutoSendDigits").Value);
        }
        finally { release.Set(); window.Close(); }
    }

    [AvaloniaFact]
    public void EscapeAndWindowCloseDiscardConfigurationEdits()
    {
        var saved = new List<SenderSettings>(); var settings = new SenderSettings(); var window = new SenderWindow(true, null, settings: settings, persistSettings: value => saved.Add(value.Clone()));
        window.Show(); OpenConfiguration(window);
        try
        {
            var config = window.OwnedWindows.Single(); Find<NumericUpDown>(config, "AutoSendDigits").Value = 2;
            Press(config, PhysicalKey.Escape); Assert.Equal(3, settings.AutoSendDigits); Assert.Empty(saved);
            OpenConfiguration(window); config = window.OwnedWindows.Single(); Find<NumericUpDown>(config, "AutoSendDigits").Value = 4;
            config.Close(); Pump(); Assert.Equal(3, settings.AutoSendDigits); Assert.Empty(saved);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void SettingsMigrateDefaultsAndRoundTripEntryAndPerPortOptions()
    {
        var settings = SenderSettings.FromJson("""{"InputId":"main","InputChannel":7,"Bindings":[{"Kind":144,"Channel":2,"Number":60,"Command":"4"}]}""");
        Assert.Equal(MidiNoteMapping.Actions, Enumerable.Range(36, 15).Select(note => settings.MidiNoteMap[note]));
        Assert.True(settings.KeyboardEntryEnabled); Assert.True(settings.MidiEntryEnabled); Assert.False(settings.AutoSend);
        Assert.Equal(35, settings.AutoSendDelayMs); Assert.Equal(3, settings.AutoSendDigits); Assert.Equal(7, settings.NoteInputChannels["main"]); Assert.Single(settings.Bindings);
        settings.AutoSend = true; settings.AutoSendDigits = 4; settings.AutoSendDelayMs = 150; settings.KeyboardEntryEnabled = false;
        settings.ThruInputs.Add(new("fm9", "FM9", 3)); settings.MidiNoteMap.Remove(36); settings.MidiNoteMap[100] = "1";
        var restored = SenderSettings.FromJson(JsonSerializer.Serialize(settings));
        Assert.True(restored.AutoSend); Assert.Equal(4, restored.AutoSendDigits); Assert.Equal(150, restored.AutoSendDelayMs); Assert.False(restored.KeyboardEntryEnabled);
        Assert.Equal(settings.ThruInputs, restored.ThruInputs); Assert.Equal("1", restored.MidiNoteMap[100]); Assert.Single(restored.Bindings);
        var corrupt = SenderSettings.FromJson("""{"AutoSendDelayMs":-1,"MidiNoteMap":{"200":"Wrong"},"ThruInputs":[null,{"Id":"fm9","Name":"FM9","Channel":99}]}""");
        Assert.Equal(10, corrupt.AutoSendDelayMs); Assert.Equal(15, corrupt.MidiNoteMap.Count); Assert.Equal(16, corrupt.ThruInputs.Single().Channel);
        Assert.Equal(1, SenderSettings.FromJson("""{"AutoSendDigits":0}""").AutoSendDigits);
        Assert.Equal(4, SenderSettings.FromJson("""{"AutoSendDigits":99}""").AutoSendDigits);
    }

    [AvaloniaFact]
    public async Task GlobalNotesRepeatRespectChannelsAndSwitchesAndLearnedControlsWin()
    {
        var midi = new TestMidi(); var settings = new SenderSettings(); var window = new SenderWindow(true, null, midi, settings);
        window.Show(); OpenConfiguration(window);
        try
        {
            await Ready(window); Find<ComboBox>(window, "MidiInput").SelectedIndex = 1;
            Click(Find<Button>(window, "ConnectMidi")); await Until(() => midi.Inputs["main"].Started);
            Find<ComboBox>(window, "InputChannel").SelectedIndex = 3;
            midi.Inputs["main"].Emit(Note(36, 2)); Pump(); Assert.Equal("---", Display(window));
            midi.Inputs["main"].Emit(Note(36, 3)); midi.Inputs["main"].Emit(Note(36, 3)); Pump(); Assert.Equal("011", Display(window));
            midi.Inputs["main"].Emit(0x92 | (36 << 8)); Pump(); Assert.Equal("011", Display(window));
            Find<CheckBox>(window, "MidiNoteEntry").IsChecked = false;
            midi.Inputs["main"].Emit(Note(37, 3)); Pump(); Assert.Equal("011", Display(window));
            Find<CheckBox>(window, "KeyboardEntry").IsChecked = false;
            Click(Find<Button>(window, "SaveConfiguration")); Find<Button>(window, "Digit1").Focus();
            Press(window, PhysicalKey.Digit9); Assert.Equal("011", Display(window));
            Click(Find<Button>(window, "ClearProgram")); Click(Find<Button>(window, "Digit7")); Assert.Equal("007", Display(window));
            OpenConfiguration(window); Find<CheckBox>(window, "MidiNoteEntry").IsChecked = true;
            Click(Find<Button>(window, "ConfigureNoteMapping"));
            Find<ComboBox>(window, "LearnCommand").SelectedIndex = 4; Click(Find<Button>(window, "LearnMidi"));
            midi.Inputs["main"].Emit(Note(36, 3)); Pump(); Assert.Equal("007", Display(window));
            midi.Inputs["main"].Emit(0x82 | (36 << 8)); midi.Inputs["main"].Emit(Note(36, 3)); midi.Inputs["main"].Emit(Note(36, 3)); Pump();
            Assert.Equal("074", Display(window)); Assert.Empty(midi.Output.Messages);
            Click(Find<Button>(window, "CancelMapping"));
            Click(Find<Button>(window, "SaveConfiguration")); Assert.Equal(3, settings.NoteInputChannels["main"]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LearningInMappingWindowKeepsKeyboardBehaviorCancelsLearnsAndReopens()
    {
        var midi = new TestMidi(); var settings = new SenderSettings(); var window = new SenderWindow(true, null, midi, settings);
        window.Show(); OpenConfiguration(window);
        try
        {
            await Ready(window); var config = window.OwnedWindows.Single();
            Click(Find<Button>(config, "ConfigureNoteMapping")); var map = config.OwnedWindows.Single();
            Find<TextBox>(map, "MappingNoteNumber").Text = "60";
            var command = Find<ComboBox>(map, "LearnCommand"); command.Focus(); Press(map, PhysicalKey.Enter);
            Assert.True(map.IsVisible); Press(map, PhysicalKey.Escape); Assert.True(map.IsVisible);
            var learn = Find<Button>(map, "LearnMidi"); learn.BringIntoView(); Pump(); learn.Focus(); Press(map, PhysicalKey.Enter);
            Assert.True(map.IsVisible);
            Assert.Contains(map.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "Connect a MIDI input before learning controls.");
            Press(map, PhysicalKey.Escape); Assert.False(map.IsVisible); Assert.Equal("1", settings.MidiNoteMap[36]); Assert.DoesNotContain(60, settings.MidiNoteMap.Keys);
            Assert.True(Find<Button>(config, "ConfigureNoteMapping").IsFocused);

            Find<ComboBox>(config, "MidiInput").SelectedIndex = 1; Click(Find<Button>(config, "ConnectMidi")); await Until(() => midi.Inputs["main"].Started);
            Click(Find<Button>(config, "ConfigureNoteMapping")); map = config.OwnedWindows.Single();
            command = Find<ComboBox>(map, "LearnCommand"); command.SelectedIndex = 4; Click(Find<Button>(map, "LearnMidi"));
            Press(map, PhysicalKey.Escape); Assert.True(map.IsVisible); Assert.True(Find<Button>(map, "LearnMidi").IsFocused);
            midi.Inputs["main"].Emit(Note(60, 1)); Pump(); Assert.Empty(settings.Bindings); Assert.Equal("---", Display(window));
            Click(Find<Button>(map, "LearnMidi")); midi.Inputs["main"].Emit(Note(60, 1)); Pump();
            Assert.Equal("Learn", Find<Button>(map, "LearnMidi").Content); Assert.Equal("---", Display(window)); Assert.Empty(midi.Output.Messages);
            Click(Find<Button>(map, "CancelMapping"));
            Click(Find<Button>(config, "ConfigureNoteMapping")); map = config.OwnedWindows.Single();
            Assert.Contains(map.GetVisualDescendants().OfType<TextBlock>(), label => label.Text?.Contains("4: Note 60", StringComparison.Ordinal) == true);
            Click(Find<Button>(map, "RemoveLearnedMapping")); Click(Find<Button>(map, "CancelMapping"));
            Click(Find<Button>(config, "CancelConfiguration")); Assert.Empty(settings.Bindings);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AutoSendWaitsForThreeDigitsAndCancelsStaleEntriesAndConfiguration()
    {
        var settings = new SenderSettings { AutoSend = true, AutoSendDelayMs = 50 }; var sent = new List<int>();
        var window = new SenderWindow(true, sent.Add, settings: settings); window.Show(); Pump();
        try
        {
            window.Execute("0"); window.Execute("4"); await Pause(); Assert.Empty(sent);
            window.Execute("2"); await Pause(); Assert.Equal(new[] { 0xb0, 0xc0 | (42 << 8) }, sent);
            window.Execute("1"); window.Execute("2"); window.Execute("7"); window.Execute("Clear"); await Pause(); Assert.Equal(2, sent.Count);
            window.Execute("1"); window.Execute("2"); window.Execute("7"); window.Execute("4"); await Pause(); Assert.Equal(2, sent.Count); Assert.Equal("004", Display(window));
            window.Execute("Clear"); window.Execute("1"); window.Execute("2"); window.Execute("7"); OpenConfiguration(window); await Pause(); Assert.Equal(2, sent.Count);
            Find<CheckBox>(window, "AutoSend").IsChecked = false; Find<NumericUpDown>(window, "AutoSendDelay").Value = 120;
            Click(Find<Button>(window, "SaveConfiguration")); window.Execute("Send"); Assert.Equal(0xc0 | (127 << 8), sent[^1]);
            Assert.False(settings.AutoSend); Assert.Equal(120, settings.AutoSendDelayMs);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ChangingAutoSendDigitCountCancelsPendingSendAndRetainsSetting()
    {
        var settings = new SenderSettings { AutoSend = true, AutoSendDelayMs = 50 }; var sent = new List<int>();
        var window = new SenderWindow(true, sent.Add, settings: settings); window.Show(); OpenConfiguration(window);
        try
        {
            window.Execute("0"); window.Execute("4"); window.Execute("2");
            var count = Find<NumericUpDown>(window, "AutoSendDigits"); count.Value = 2; await Pause(); Assert.Empty(sent);
            window.Execute("4"); window.Execute("2"); await Pause(); Assert.Equal(new[] { 0xb0, 0xc0 | (42 << 8) }, sent);
            count.Value = 4; window.Execute("1"); window.Execute("0"); window.Execute("2"); window.Execute("4"); await Pause(); Assert.Equal(2, sent.Count);
            window.Execute("0"); window.Execute("0"); window.Execute("4"); window.Execute("2"); await Pause(); Assert.Equal(4, sent.Count); Assert.Equal(0xc0 | (42 << 8), sent[^1]);
            Click(Find<Button>(window, "SaveConfiguration")); Assert.Equal(4, settings.AutoSendDigits);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1, "7", 7)]
    [InlineData(2, "42", 42)]
    [InlineData(3, "042", 42)]
    [InlineData(4, "0042", 42)]
    public async Task KeyboardAutoSendsOnlyAtConfiguredDigitCount(int digits, string entry, int expected)
    {
        var sent = new List<int>(); var window = new SenderWindow(true, sent.Add, settings: new SenderSettings { AutoSend = true, AutoSendDigits = digits, AutoSendDelayMs = 50 });
        window.Show(); Pump();
        try
        {
            foreach (char digit in entry[..^1]) { Press(window, Enum.Parse<PhysicalKey>("Digit" + digit)); }
            await Pause(); Assert.Empty(sent);
            Press(window, Enum.Parse<PhysicalKey>("Digit" + entry[^1])); await Pause();
            Assert.Equal(new[] { 0xb0, 0xc0 | (expected << 8) }, sent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ManualEntryUsesChangedDigitCountWhenAutoSendIsOff()
    {
        var settings = new SenderSettings(); var sent = new List<int>();
        var window = new SenderWindow(true, sent.Add, settings: settings); window.Show(); OpenConfiguration(window);
        try
        {
            Find<NumericUpDown>(window, "AutoSendDigits").Value = 2;
            Click(Find<Button>(window, "SaveConfiguration"));
            Press(window, PhysicalKey.Digit4); Press(window, PhysicalKey.Digit2); Press(window, PhysicalKey.Digit7);
            await Pause(); Assert.Empty(sent); Assert.Equal("42", Display(window));
            Press(window, PhysicalKey.Enter); Assert.Equal(new[] { 0xb0, 0xc0 | (42 << 8) }, sent); Assert.False(settings.AutoSend);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1, "7", 7)]
    [InlineData(2, "42", 42)]
    [InlineData(3, "042", 42)]
    [InlineData(4, "0042", 42)]
    public async Task ManualEntryRespectsConfiguredLimitAndWaitsForConfirmation(int digits, string entry, int expected)
    {
        var sent = new List<int>(); var window = new SenderWindow(true, sent.Add, settings: new SenderSettings { AutoSend = false, AutoSendDigits = digits });
        window.Show(); Pump();
        try
        {
            foreach (char digit in entry) { Press(window, Enum.Parse<PhysicalKey>("Digit" + digit)); }
            Click(Find<Button>(window, "Digit9")); await Pause(); Assert.Empty(sent); Assert.Equal(entry, Display(window));
            Click(Find<Button>(window, "SendProgram")); Assert.Equal(new[] { 0xb0, 0xc0 | (expected << 8) }, sent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ThruForwardsOriginalChannelsFiltersOnlyNoteEntryAndCanChangeLive()
    {
        var midi = new TestMidi(); var settings = new SenderSettings(); var window = new SenderWindow(true, null, midi, settings);
        window.Show(); OpenConfiguration(window);
        try
        {
            await Ready(window); Find<ComboBox>(window, "MidiInput").SelectedIndex = 1; Pump();
            var checks = Controls<CheckBox>(window).Where(c => c.Name == "ThruInput").ToArray();
            checks.Single(c => Equals(c.Content, "FM9")).IsChecked = true;
            Controls<ComboBox>(window).First(c => c.Name == "ThruNoteChannel").SelectedIndex = 3;
            Click(Find<Button>(window, "ConnectMidi")); await Until(() => midi.Inputs["fm9"].Started);
            int cc = 0xb8 | (20 << 8) | (64 << 16), filtered = Note(36, 2), accepted = Note(37, 3);
            midi.Inputs["fm9"].Emit(cc); midi.Inputs["fm9"].Emit(filtered); midi.Inputs["fm9"].Emit(accepted); midi.Inputs["fm9"].Emit(0xf8); Pump();
            midi.Inputs["fm9"].Emit(0x82 | (37 << 8)); midi.Inputs["fm9"].Emit(0xf2); midi.Inputs["fm9"].Emit(0xfa); Pump();
            Assert.Equal(new[] { cc, filtered }, midi.Output.Messages); Assert.Equal("002", Display(window));
            Controls<CheckBox>(window).Single(c => c.Name == "ThruInput" && Equals(c.Content, "Foot controller")).IsChecked = true;
            Assert.True(midi.Inputs["foot"].Started);
            var queued = midi.Inputs["fm9"].Snapshot();
            Controls<CheckBox>(window).Single(c => c.Name == "ThruInput" && Equals(c.Content, "FM9")).IsChecked = false;
            Assert.True(midi.Inputs["fm9"].Disposed); Assert.Contains(0x81 | (36 << 8), midi.Output.Messages); Assert.DoesNotContain(0x82 | (37 << 8), midi.Output.Messages);
            int count = midi.Output.Messages.Count; queued(Note(38, 3)); Pump(); Assert.Equal(count, midi.Output.Messages.Count);
            midi.Inputs["foot"].Emit(Note(45, 10)); Pump(); Assert.Equal("020", Display(window));
            Click(Find<Button>(window, "MidiDiagnostics")); Pump();
            var diagnostics = window.OwnedWindows.Single().OwnedWindows.Single();
            Assert.Contains("Thru", Find<TextBox>(diagnostics, "MidiActivity").Text);
            Click(Find<Button>(diagnostics, "CloseDiagnostics"));
            Click(Find<Button>(window, "DisconnectMidi")); Assert.True(midi.Inputs["foot"].Disposed); Assert.True(midi.Output.Disposed);
            Assert.Equal("foot", settings.ThruInputs.Single().Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedThruStartupReleasesEveryPortAndQueuedMessagesAreIgnored()
    {
        var midi = new TestMidi(); midi.Inputs["foot"].FailStart = true;
        var settings = new SenderSettings { ThruInputs = [new("fm9", "FM9"), new("foot", "Foot controller")] };
        var window = new SenderWindow(true, null, midi, settings); window.Show(); OpenConfiguration(window);
        try
        {
            await Ready(window); Find<ComboBox>(window, "MidiInput").SelectedIndex = 1;
            window.Execute("4"); Click(Find<Button>(window, "ConnectMidi")); await Until(() => midi.Output.Disposed);
            Assert.All(midi.Inputs.Values, input => Assert.True(input.Disposed)); Assert.Equal("004", Display(window));
            Assert.True(Find<Button>(window, "ConnectMidi").IsEnabled); Assert.False(Find<Button>(window, "DisconnectMidi").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MappingEditsArePrivateRejectDuplicatesAndSurviveSaveFailure()
    {
        var original = MidiNoteMapping.Defaults(); Dictionary<int, string>? saved = null; bool fail = true;
        var dialog = new MidiMappingWindow(original, map => { if (fail) { throw new IOException("disk full"); } saved = map; });
        dialog.Show(); Pump();
        try
        {
            var note = Find<TextBox>(dialog, "MappingNoteNumber"); note.Text = "37"; Click(Find<Button>(dialog, "MapActionSend"));
            Assert.Contains("already assigned", Find<TextBlock>(dialog, "MappingMessage").Text); Assert.Equal("1", original[36]); Assert.Null(saved);
            note.Text = "128"; Click(Find<Button>(dialog, "SaveMapping")); Assert.Null(saved);
            note.Text = "60"; Click(Find<Button>(dialog, "MapActionSend")); note.Text = "61"; Click(Find<Button>(dialog, "SaveMapping"));
            Assert.Contains("disk full", Find<TextBlock>(dialog, "MappingMessage").Text); Assert.True(dialog.IsVisible); Assert.Equal("Send", original[46]);
            fail = false; Click(Find<Button>(dialog, "SaveMapping")); Assert.NotNull(saved); Assert.Equal(15, saved.Count); Assert.Equal("1", saved[60]); Assert.Equal("Send", saved[61]);
        }
        finally { dialog.Close(); }
        var cancel = new MidiMappingWindow(original, _ => throw new InvalidOperationException("Cancel must not save")); cancel.Show(); Pump();
        Find<TextBox>(cancel, "MappingNoteNumber").Text = "80"; Press(cancel, PhysicalKey.Escape);
        Assert.False(cancel.IsVisible); Assert.Equal("1", original[36]);
    }

    [AvaloniaTheory]
    [InlineData(1040, 700, "Light")]
    [InlineData(1040, 700, "Dark")]
    [InlineData(760, 780, "Light")]
    [InlineData(400, 440, "Light")]
    [InlineData(760, 780, "Dark")]
    [InlineData(400, 440, "Dark")]
    public async Task RenderConfigurationMappingAndDiagnosticsAndReturnFocus(int width, int height, string theme)
    {
        var window = new SenderWindow(true, null, new TestMidi(singleInput: true)) { RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show(); OpenConfiguration(window);
        try
        {
            await Ready(window); var config = window.OwnedWindows.Single(); config.Width = width; config.Height = height; Pump();
            Capture(config, $"config-{width}-{theme}");
            Find<Button>(config, "ConfigureNoteMapping").BringIntoView(); Pump(); Capture(config, $"entry-{width}-{theme}");
            Click(Find<Button>(config, "ConfigureNoteMapping")); var map = config.OwnedWindows.Single();
            map.Width = width == 400 ? 360 : 450; map.Height = width == 400 ? 400 : 760; Pump(); Capture(map, $"mapping-{width}-{theme}");
            var scroll = map.GetVisualDescendants().OfType<ScrollViewer>().First(); scroll.Offset = new Vector(0, scroll.Extent.Height); Pump(); Capture(map, $"mapping-bottom-{width}-{theme}");
            Find<TextBox>(map, "MappingNoteNumber").Text = "60"; Click(Find<Button>(map, "SaveMapping")); Pump();
            Assert.True(Find<Button>(config, "ConfigureNoteMapping").IsFocused);
            Click(Find<Button>(config, "ConfigureNoteMapping")); map = config.OwnedWindows.Single(); Assert.Equal("60", Find<TextBox>(map, "MappingNoteNumber").Text);
            Press(map, PhysicalKey.Escape); Assert.True(Find<Button>(config, "ConfigureNoteMapping").IsFocused);
            Pump(); Find<ComboBox>(config, "OutputChannel").BringIntoView(); Pump(); Capture(config, $"additional-{width}-{theme}");
            var configurationScroll = config.GetVisualDescendants().OfType<ScrollViewer>().First();
            configurationScroll.Offset = new Vector(0, configurationScroll.Extent.Height); Pump(); Capture(config, $"additional-bottom-{width}-{theme}");
            Click(Find<Button>(config, "MidiDiagnostics")); var diagnostics = config.OwnedWindows.Single();
            diagnostics.Width = width == 400 ? 360 : 600; Pump(); Capture(diagnostics, $"diagnostics-{width}-{theme}"); Press(diagnostics, PhysicalKey.Escape);
            Press(config, PhysicalKey.Escape); Assert.True(Find<Button>(window, "OpenConfiguration").IsFocused);
        }
        finally { window.Close(); }
    }

    private static int Note(int note, int channel) => 0x90 | (channel - 1) | (note << 8) | (100 << 16);
    private static string? Display(Window window) => Find<TextBlock>(window, "ProgramDisplay").Text;
    private static IEnumerable<T> Controls<T>(Window window) where T : Control => window.GetVisualDescendants().OfType<T>().Concat(window.OwnedWindows.SelectMany(Controls<T>));
    private static T Find<T>(Window window, string name) where T : Control => Controls<T>(window).First(c => c.Name == name);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void OpenConfiguration(Window window) => Click(Find<Button>(window, "OpenConfiguration"));
    private static void Press(Window window, PhysicalKey key) { window.KeyPressQwerty(key, Avalonia.Input.RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, Avalonia.Input.RawInputModifiers.None); } Pump(); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
    private static async Task Pause() { await Task.Delay(100); Pump(); }
    private static Task Ready(Window window) => Until(() => Find<ComboBox>(window, "MidiOutput").ItemCount > 0 && Find<Button>(window, "ConnectMidi").IsEnabled);
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { Pump(); if (condition()) { return; } await Task.Delay(10); }
        Assert.True(condition(), "Timed out waiting for MIDI setup.");
    }
    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("PRESET_SENDER_CONFIG_SCREENSHOT_DIR"); if (directory is null) { return; }
        Directory.CreateDirectory(directory); Pump(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(directory, name + ".png"));
    }

    private sealed class TestMidi(bool singleInput = false) : ISenderMidiBackend
    {
        public Dictionary<string, TestInput> Inputs { get; } = new() { ["main"] = new(), ["fm9"] = new(), ["foot"] = new() };
        public TestOutput Output { get; } = new();
        public Action? OpeningOutput { get; init; }
        public IReadOnlyList<(string Id, string Name)> GetInputDevices() => singleInput ? [("fm9", "FM9")] : [("main", "Main controller"), ("fm9", "FM9"), ("foot", "Foot controller")];
        public IReadOnlyList<(int Index, string Name)> GetOutputDevices() => [(0, "Test receiver")];
        public IMidiInputPort OpenInput(string id) => Inputs[id]; public IMidiOutputPort OpenOutput(int index) { OpeningOutput?.Invoke(); return Output; }
        public bool SupportsVirtualOutput => false; public IMidiOutputPort OpenVirtualOutput() => throw new NotSupportedException();
    }
    private sealed class TestInput : IMidiInputPort
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailStart { get; set; }
        public event EventHandler<MidiShortMessageEventArgs>? MessageReceived;
        public event EventHandler<MidiSysExMessageEventArgs>? SysexMessageReceived { add { } remove { } }
        public event EventHandler<string>? ErrorReceived { add { } remove { } }
        public void Emit(int raw) => MessageReceived?.Invoke(this, new(raw));
        public Action<int> Snapshot() { var handler = MessageReceived; return raw => handler?.Invoke(this, new(raw)); }
        public void Start() { if (FailStart) { throw new IOException("Startup failure"); } Started = true; }
        public void Stop() => Started = false; public void Dispose() => Disposed = true;
    }
    private sealed class TestOutput : IMidiOutputPort
    {
        public List<int> Messages { get; } = []; public bool Disposed { get; private set; }
        public void Send(int message) => Messages.Add(message); public void SendBuffer(byte[] frame) => throw new NotSupportedException(); public void Dispose() => Disposed = true;
    }
}
