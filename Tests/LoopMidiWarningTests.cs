#if SENDER_WINDOWS
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
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

public sealed class LoopMidiWarningTests
{
    [Theory]
    [InlineData("loopMIDI", true)]
    [InlineData("loopMIDI 1.0.16", true)]
    [InlineData("LOOPMIDI", true)]
    [InlineData("loopMIDI 1.0.16.27", true)]
    [InlineData("Other MIDI driver", false)]
    [InlineData("loopMIDIHelper", false)]
    [InlineData("", false)]
    public void InstallationCheckUsesProductMetadata(string product, bool installed)
    {
        Assert.Equal(installed ? LoopMidiStatus.Installed : LoopMidiStatus.Missing,
            LoopMidiInstallation.Inspect(() => ["Unrelated application", product]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnreadableInstallationMetadataDoesNotClaimLoopMidiIsMissing(int failure)
    {
        Assert.Equal(LoopMidiStatus.Unknown, LoopMidiInstallation.Inspect(() => throw (failure switch
        {
            0 => new UnauthorizedAccessException(),
            1 => new System.Security.SecurityException(),
            _ => new IOException()
        })));
    }

    [Fact]
    public void RoutingIntentDefaultsToHardwareAndSurvivesSavedSettings()
    {
        Assert.False(SenderSettings.FromJson("{}").AppToAppRouting);
        var original = new SenderSettings { AppToAppRouting = true };
        Assert.True(original.Clone().AppToAppRouting);
        Assert.True(SenderSettings.FromJson(JsonSerializer.Serialize(original)).AppToAppRouting);
        var restored = new SenderSettings(); restored.CopyFrom(original);
        Assert.True(restored.AppToAppRouting);
        restored.CopyFrom(new SenderSettings()); Assert.False(restored.AppToAppRouting);
    }

    [AvaloniaFact]
    public async Task MissingLoopMidiOnlyWarnsWhenVirtualOutputHelpIsRequested()
    {
        var backend = new MetadataOnlyBackend { Status = LoopMidiStatus.Missing };
        var settings = new SenderSettings(); var window = new SenderWindow(true, null, backend, settings);
        window.Show(); OpenConfiguration(window);
        try
        {
            var config = window.OwnedWindows.Single(); await Ready(config);
            var intent = Find<CheckBox>(config, "AppToAppRouting");
            var warning = Find<Border>(config, "LoopMidiWarning");
            var output = Find<ComboBox>(config, "MidiOutput"); var selected = output.SelectedItem;
            Assert.Equal("FM9 MIDI Out", selected?.ToString());
            Assert.True(intent.IsVisible); Assert.False(warning.IsVisible);
            Assert.Contains(config.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Direct hardware output does not need loopMIDI.");
            intent.Focus(); Press(config, PhysicalKey.Space);
            Assert.True(settings.AppToAppRouting); Assert.True(warning.IsVisible);
            Assert.Equal("Virtual MIDI output", AutomationProperties.GetName(intent));
            Assert.True(Find<Button>(config, "ConnectMidi").IsEnabled);
            Assert.Same(selected, output.SelectedItem);
            Click(Find<Button>(config, "DismissLoopMidiWarning"));
            Assert.False(warning.IsVisible); Assert.True(intent.IsFocused);
            Click(Find<Button>(config, "SaveConfiguration")); OpenConfiguration(window);
            config = window.OwnedWindows.Single(); Assert.True(warning.IsVisible);
            intent.IsChecked = false; Assert.False(warning.IsVisible);
            Assert.Equal(0, backend.OpenAttempts);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData((int)LoopMidiStatus.Installed, true)]
    [InlineData((int)LoopMidiStatus.Unknown, true)]
    [InlineData((int)LoopMidiStatus.NotApplicable, false)]
    public async Task InstalledUnknownAndOtherPlatformsDoNotShowMissingWarning(int status, bool showIntent)
    {
        var backend = new MetadataOnlyBackend { Status = (LoopMidiStatus)status };
        var window = new SenderWindow(true, null, backend, new SenderSettings { AppToAppRouting = true });
        window.Show(); OpenConfiguration(window);
        try
        {
            var config = window.OwnedWindows.Single(); await Ready(config);
            Assert.Equal(showIntent, Find<CheckBox>(config, "AppToAppRouting").IsVisible);
            Assert.False(Find<Border>(config, "LoopMidiWarning").IsVisible);
            Assert.Equal(0, backend.OpenAttempts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RefreshRechecksInstallationWithoutChangingOutputOrOpeningPorts()
    {
        var backend = new MetadataOnlyBackend { Status = LoopMidiStatus.Missing };
        var window = new SenderWindow(true, null, backend, new SenderSettings { AppToAppRouting = true });
        window.Show(); OpenConfiguration(window);
        try
        {
            var config = window.OwnedWindows.Single(); await Ready(config);
            Assert.True(Find<Border>(config, "LoopMidiWarning").IsVisible);
            int checks = backend.StatusChecks; backend.Status = LoopMidiStatus.Installed;
            Click(Find<Button>(config, "RefreshLoopMidi"));
            await Until(() => backend.StatusChecks > checks && Find<Button>(config, "RefreshMidi").IsEnabled);
            Assert.False(Find<Border>(config, "LoopMidiWarning").IsVisible);
            Assert.Equal("FM9 MIDI Out", Find<ComboBox>(config, "MidiOutput").SelectedItem?.ToString());
            checks = backend.StatusChecks; backend.Status = LoopMidiStatus.Missing;
            Click(Find<Button>(config, "RefreshMidi"));
            await Until(() => backend.StatusChecks > checks && Find<Button>(config, "RefreshMidi").IsEnabled);
            Assert.True(Find<Border>(config, "LoopMidiWarning").IsVisible);
            Assert.Equal(0, backend.OpenAttempts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SaveKeepsRoutingIntentAndCancelRestoresIt()
    {
        var backend = new MetadataOnlyBackend { Status = LoopMidiStatus.Missing };
        var settings = new SenderSettings(); var saved = new List<SenderSettings>();
        var window = new SenderWindow(true, null, backend, settings, value => saved.Add(value.Clone()));
        window.Show(); OpenConfiguration(window);
        try
        {
            var config = window.OwnedWindows.Single(); await Ready(config);
            Find<CheckBox>(config, "AppToAppRouting").IsChecked = true;
            Click(Find<Button>(config, "CancelConfiguration")); Assert.False(settings.AppToAppRouting); Assert.Empty(saved);
            OpenConfiguration(window); config = window.OwnedWindows.Single();
            Assert.False(Find<Border>(config, "LoopMidiWarning").IsVisible);
            Find<CheckBox>(config, "AppToAppRouting").IsChecked = true;
            Click(Find<Button>(config, "SaveConfiguration")); Assert.True(saved.Single().AppToAppRouting);
            OpenConfiguration(window); config = window.OwnedWindows.Single();
            Find<CheckBox>(config, "AppToAppRouting").IsChecked = false;
            Press(config, PhysicalKey.Escape); Assert.True(settings.AppToAppRouting);
            OpenConfiguration(window); config = window.OwnedWindows.Single();
            Assert.True(Find<CheckBox>(config, "AppToAppRouting").IsChecked);
            Assert.True(Find<Border>(config, "LoopMidiWarning").IsVisible);
            Assert.Equal(0, backend.OpenAttempts);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1040, 700, "Light")]
    [InlineData(1040, 700, "Dark")]
    [InlineData(400, 440, "Light")]
    [InlineData(400, 440, "Dark")]
    public async Task WarningRemainsReadableWithKeyboardDismissal(int width, int height, string theme)
    {
        var backend = new MetadataOnlyBackend { Status = LoopMidiStatus.Missing };
        var window = new SenderWindow(true, null, backend, new SenderSettings { AppToAppRouting = true })
        { RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show(); OpenConfiguration(window);
        try
        {
            var config = window.OwnedWindows.Single(); await Ready(config);
            config.Width = width; config.Height = height; Pump();
            string? directory = Environment.GetEnvironmentVariable("PRESET_SENDER_WARNING_SCREENSHOT_DIR");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory); using var frame = config.CaptureRenderedFrame();
                Assert.NotNull(frame); frame.Save(Path.Combine(directory, $"warning-{width}-{theme}.png"));
            }
            var dismiss = Find<Button>(config, "DismissLoopMidiWarning"); dismiss.BringIntoView(); Pump();
            dismiss.Focus(); Press(config, PhysicalKey.Enter);
            Assert.False(Find<Border>(config, "LoopMidiWarning").IsVisible);
            Assert.True(Find<CheckBox>(config, "AppToAppRouting").IsFocused);
            Press(config, PhysicalKey.Escape);
            Assert.False(config.IsVisible); Assert.True(Find<Button>(window, "OpenConfiguration").IsFocused);
            Assert.Equal(0, backend.OpenAttempts);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void OpenConfiguration(Window window) => Click(Find<Button>(window, "OpenConfiguration"));
    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); }
        Pump();
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
    private static Task Ready(Window config) => Until(() => Find<ComboBox>(config, "MidiOutput").ItemCount > 0 && Find<Button>(config, "ConnectMidi").IsEnabled);
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { Pump(); if (condition()) { return; } await Task.Delay(10); }
        Assert.True(condition(), "Timed out waiting for installation status.");
    }

    // Descriptors and installation status only. Opening any MIDI port fails the check.
    private sealed class MetadataOnlyBackend : ISenderMidiBackend
    {
        public LoopMidiStatus Status { get; set; }
        public int StatusChecks { get; private set; }
        public int OpenAttempts { get; private set; }
        public LoopMidiStatus GetLoopMidiStatus() { StatusChecks++; return Status; }
        public IReadOnlyList<(string Id, string Name)> GetInputDevices() => [];
        public IReadOnlyList<(int Index, string Name)> GetOutputDevices() => [(0, "FM9 MIDI Out")];
        public bool SupportsVirtualOutput => false;
        public IMidiInputPort OpenInput(string id) { OpenAttempts++; throw new InvalidOperationException("This check must not open MIDI ports."); }
        public IMidiOutputPort OpenOutput(int index) { OpenAttempts++; throw new InvalidOperationException("This check must not open MIDI ports."); }
        public IMidiOutputPort OpenVirtualOutput() { OpenAttempts++; throw new InvalidOperationException("This check must not create MIDI ports."); }
    }
}
#endif
