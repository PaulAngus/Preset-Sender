using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace PresetSender;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if SENDER_WINDOWS
        BundledMidiApi.Prepare();
        if (args.Contains("--dependency-report", StringComparer.Ordinal))
        {
            Environment.ExitCode = MightBeUseful.Midi.Windows.WindowsMidiDependencyReport.Write(Console.Out);
            return;
        }
#endif
        if (args.Contains("--diagnostics-json", StringComparer.Ordinal))
        {
#if SENDER_WINDOWS
            var capabilities = MightBeUseful.Midi.Windows.WindowsMidiBackend.Capabilities;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                platform = "Windows",
                osVersion = Environment.OSVersion.Version.ToString(),
                backend = capabilities.Description,
                canCreateVirtualPorts = capabilities.CanCreateVirtualPorts,
                useMidiServices = capabilities.UseMidiServices,
                apiDirectory = MightBeUseful.Midi.Windows.WindowsMidiBackend.ApiDirectory,
                preparationError = BundledMidiApi.PreparationError
            }));
#else
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                platform = "macOS",
                osVersion = Environment.OSVersion.Version.ToString(),
                backend = "MightBeUseful.Midi.CoreMidi",
                canCreateVirtualPorts = false,
                useMidiServices = false
            }));
#endif
            return;
        }
        if (args.Contains("--midi-backend", StringComparer.Ordinal))
        {
#if SENDER_WINDOWS
            Console.WriteLine(MightBeUseful.Midi.Windows.WindowsMidiBackend.Capabilities.Description);
            Console.WriteLine($"MIDI API directory: {MightBeUseful.Midi.Windows.WindowsMidiBackend.ApiDirectory}");
#else
            Console.WriteLine("CoreMIDI MIDI 1.0 (MightBeUseful.Midi.CoreMidi).");
#endif
            return;
        }
        if (args.Contains("--list-ports", StringComparer.Ordinal))
        {
#if SENDER_WINDOWS
            MightBeUseful.Midi.Windows.WindowsMidiBackend.Print(Console.Out);
#else
            MightBeUseful.Midi.CoreMidi.CoreMidiPortCatalog.Print(Console.Out);
#endif
            return;
        }
#if SENDER_WINDOWS
        if (args is ["--virtual-midi-host"])
        {
            // MIDI initialization runs in an MTA, without starting Avalonia.
            Task.Run(VirtualMidiHost.Run).GetAwaiter().GetResult();
            return;
        }
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<SenderApp>();
#if SENDER_WINDOWS
        builder.UseWin32();
#else
        builder.UseAvaloniaNative();
#endif
        return builder.UseSkia().LogToTrace();
    }
}

public sealed class SenderApp : Application
{
    public override void Initialize()
    {
        Name = "Preset Sender";
        Styles.Add(new FluentTheme());
        // Use the app's light/dark palette and control shape with Fluent interaction states.
        Resources.ThemeDictionaries[ThemeVariant.Light] = Palette("#E9EDF0", "#FFFFFF", "#F4F6F8", "#CDD5DC", "#17232D");
        Resources.ThemeDictionaries[ThemeVariant.Dark] = Palette("#11151B", "#181D24", "#11161D", "#303844", "#F2F5F8");
        Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, new DynamicResourceExtension("SenderSurface")),
                new Setter(Button.ForegroundProperty, new DynamicResourceExtension("SenderText")),
                new Setter(Button.BorderBrushProperty, new DynamicResourceExtension("SenderBorder")),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(9))
            }
        });
    }

    private static ResourceDictionary Palette(string background, string surface, string inset, string border, string text) => new()
    {
        ["SenderBackground"] = SolidColorBrush.Parse(background),
        ["SenderSurface"] = SolidColorBrush.Parse(surface),
        ["SenderInset"] = SolidColorBrush.Parse(inset),
        ["SenderBorder"] = SolidColorBrush.Parse(border),
        ["SenderText"] = SolidColorBrush.Parse(text),
        ["SenderAccent"] = SolidColorBrush.Parse(background == "#11151B" ? "#2F80ED" : "#1969B0"),
        ["SenderSecondary"] = SolidColorBrush.Parse(background == "#11151B" ? "#A7B0BC" : "#5F6B75"),
        ["SenderDanger"] = SolidColorBrush.Parse(background == "#11151B" ? "#EC6C68" : "#C84444"),
        ["SenderWarningBackground"] = SolidColorBrush.Parse(background == "#11151B" ? "#433519" : "#FFF4CE"),
        ["SenderWarningText"] = SolidColorBrush.Parse(background == "#11151B" ? "#FFD98A" : "#5E4000")
    };

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new SenderWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
