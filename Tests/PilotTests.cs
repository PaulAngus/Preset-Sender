using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PresetSender.Tests.Support;

namespace PresetSender.Tests;

public sealed class PilotTests
{
#if SENDER_WINDOWS
    [Fact]
    public void TestVirtualNamesRequireAnIsolatedProfileAndSafePrefix()
    {
        Assert.Equal("Preset Sender", WindowsVirtualMidiOutput.ResolveName(null, null));
        Assert.Throws<ArgumentException>(() => WindowsVirtualMidiOutput.ResolveName("PresetSender-Test-example", null));
        string profile = Path.Combine(Path.GetTempPath(), "sender-pilot-profile");
        Assert.Throws<ArgumentException>(() => WindowsVirtualMidiOutput.ResolveName("User device", profile));
        Assert.Throws<ArgumentException>(() => WindowsVirtualMidiOutput.ResolveName("PresetSender-Test-" + new string('x', 20), profile));
        Assert.Equal("PresetSender-Test-example", WindowsVirtualMidiOutput.ResolveName("PresetSender-Test-example", profile));
    }
#endif

    [Fact]
    public void IsolatedSettingsRequireAnAbsoluteDirectory()
    {
        Assert.Throws<ArgumentException>(() => SenderSettings.ResolveDirectory("relative-profile"));
        string directory = Path.Combine(Path.GetTempPath(), "sender-pilot-profile");
        Assert.Equal(Path.GetFullPath(directory), SenderSettings.ResolveDirectory(directory));
    }

    [AvaloniaFact]
    public async Task InputErrorReleasesPortsAndLateInputCannotSend()
    {
        var midi = new CapturedMidiBackend();
        var window = new SenderWindow(true, null, midi, new SenderSettings { InputId = "controller", OutputName = "Test receiver", VirtualOutput = false });
        window.Show();
        try
        {
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenConfiguration").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var configuration = window.OwnedWindows.Single();
            await Until(() => configuration.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RefreshMidi").IsEnabled && midi.RefreshCount > 0);
            configuration.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConnectMidi").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => midi.Input.Started);
            window.Execute("4"); window.Execute("2");
            midi.Input.Fail("Simulated unplug");
            await Until(() => midi.Output.Disposed);
            midi.Input.Emit(0x90 | (60 << 8) | (100 << 16));
            Dispatcher.UIThread.RunJobs();
            Assert.True(midi.Input.Disposed);
            Assert.Empty(midi.Output.Messages);
            Assert.Equal("042", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ProgramDisplay").Text);
        }
        finally { window.Close(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) { return; }
            await Task.Delay(10, timeout.Token);
        }
    }
}
