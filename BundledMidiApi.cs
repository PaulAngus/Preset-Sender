#if SENDER_WINDOWS
using System.Security.Cryptography;
using MightBeUseful.Midi.Windows;

namespace PresetSender;

internal static class BundledMidiApi
{
    private const string ApiVersion = "0.99.88-preview.10";
    internal static string? PreparationError { get; private set; }

    internal static void Prepare()
    {
        // Do not extract or activate the native API on Windows 10/older Windows 11.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26200)
            || Environment.GetEnvironmentVariable("MBU_MIDI_API_DIRECTORY") is not null) { return; }

        string root = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PresetSender", "Midi");
        string directory = Path.Combine(root, "windows-midi", ApiVersion, "x64");
        WindowsMidiBackend.ConfigureAppLocalApiDirectory(directory);
        try
        {
            Directory.CreateDirectory(directory);
            Extract("Windows.Devices.Midi2.dll", directory);
            Extract("Windows.Devices.Midi2.pri", directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Keep hardware/legacy routing available; detection reports the missing
            // component and the diagnostic record retains the extraction error.
            PreparationError = error.Message;
        }
    }

    private static void Extract(string name, string directory)
    {
        using var resource = typeof(BundledMidiApi).Assembly.GetManifestResourceStream("PresetSender.MidiApi." + name)
            ?? throw new IOException("The executable is missing its bundled MIDI API: " + name);
        byte[] expected = SHA256.HashData(resource);
        string destination = Path.Combine(directory, name);
        if (Matches(destination, expected)) { return; }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            resource.Position = 0;
            using (var output = File.Create(temporary)) { resource.CopyTo(output); }
            try { File.Move(temporary, destination, overwrite: true); }
            catch (IOException) when (Matches(destination, expected)) { } // Another instance extracted first.
            catch (UnauthorizedAccessException) when (Matches(destination, expected)) { } // Another instance already loaded it.
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static bool Matches(string path, byte[] expected)
    {
        if (!File.Exists(path)) { return false; }
        using var input = File.OpenRead(path);
        return SHA256.HashData(input).AsSpan().SequenceEqual(expected);
    }
}
#endif
