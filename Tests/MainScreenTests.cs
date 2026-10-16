using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PresetSender.Tests;

public sealed class MainScreenTests
{
    [AvaloniaTheory]
    [InlineData(460, 740, "Light")]
    [InlineData(460, 740, "Dark")]
    [InlineData(360, 520, "Light")]
    [InlineData(360, 520, "Dark")]
    public void ConfigureKeepsEntryAndSettingsReturnsFocusAndMainButtonsSend(int width, int height, string theme)
    {
        var writes = new List<int>();
        var owner = new SenderWindow(true, writes.Add) { Width = width, Height = height, RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark };
        owner.Show(); Pump();
        string? captureDirectory = Environment.GetEnvironmentVariable("PRESET_SENDER_CONFIG_SCREENSHOT_DIR");
        if (captureDirectory is not null) { Directory.CreateDirectory(captureDirectory); using var frame = owner.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(captureDirectory, $"main-{width}-{theme}.png")); }
        try
        {
            owner.Execute("4"); owner.Execute("2");
            var configure = Find<Button>(owner, "OpenConfiguration"); configure.Focus(); Press(owner, PhysicalKey.Enter);
            var configuration = owner.OwnedWindows.Single();
            Assert.Equal("042", Find<TextBlock>(owner, "ProgramDisplay").Text);
            Assert.True(Find<Button>(configuration, "CancelConfiguration").IsFocused);
            Click(configure); Assert.Same(configuration, owner.OwnedWindows.Single());
            Pump();
            var channel = Find<ComboBox>(configuration, "OutputChannel"); channel.SelectedIndex = 6; channel.BringIntoView(); Pump(); channel.Focus();
            Press(configuration, PhysicalKey.Digit9); Assert.Empty(writes); Assert.Equal("042", Find<TextBlock>(owner, "ProgramDisplay").Text);
            channel.SelectedIndex = 6;
            var close = Find<Button>(configuration, "CancelConfiguration"); var closePosition = close.TranslatePoint(default, configuration);
            var scroll = configuration.GetVisualDescendants().OfType<ScrollViewer>().First(); scroll.Offset = new Vector(0, scroll.Extent.Height); Pump();
            Assert.Equal(closePosition, close.TranslatePoint(default, configuration));
            channel.IsDropDownOpen = true; Pump(); Press(configuration, PhysicalKey.Escape);
            Assert.True(configuration.IsVisible); Assert.False(channel.IsDropDownOpen);
            Click(Find<Button>(configuration, "SaveConfiguration")); Assert.False(configuration.IsVisible); Assert.True(configure.IsFocused);
            owner.Execute("Send"); Assert.Equal(new[] { 0xc5 | (42 << 8) }, writes);
            Click(Find<Button>(owner, "NextProgram")); Assert.Equal(0xc5 | (43 << 8), writes[^1]);
            Click(Find<Button>(owner, "PrevProgram")); Assert.Equal(0xc5 | (42 << 8), writes[^1]);
            Click(Find<Button>(owner, "LastProgram")); Assert.Equal(0xc5 | (43 << 8), writes[^1]);
            Click(configure); configuration = owner.OwnedWindows.Single();
            Assert.Equal(6, Find<ComboBox>(configuration, "OutputChannel").SelectedIndex);
            Click(Find<Button>(configuration, "CancelConfiguration"));
            Press(owner, PhysicalKey.Digit7); Press(owner, PhysicalKey.Enter); Assert.Equal(0xc5 | (7 << 8), writes[^1]);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public void NavigationWrapsAndLastTracksOnlySuccessfulSends()
    {
        var writes = new List<int>(); bool fail = false;
        var window = new SenderWindow(true, message => { if (fail) { throw new IOException("Send failed"); } writes.Add(message); });
        try
        {
            window.Execute("Last"); Assert.Empty(writes);
            window.Execute("Prev"); Assert.Equal(0xc0 | (127 << 8), writes[^1]);
            window.Execute("Next"); Assert.Equal(0xc0, writes[^1]);
            fail = true; window.Execute("Next"); Assert.Equal(2, writes.Count);
            fail = false; window.Execute("Last"); Assert.Equal(0xc0 | (127 << 8), writes[^1]);
            window.Execute("Last"); Assert.Equal(0xc0, writes[^1]);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Press(Window window, PhysicalKey key) { window.KeyPressQwerty(key, Avalonia.Input.RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, Avalonia.Input.RawInputModifiers.None); } Pump(); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
}
