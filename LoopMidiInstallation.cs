#if SENDER_WINDOWS
using Microsoft.Win32;

namespace PresetSender;

// Installation metadata only: never invokes MSI, starts software or creates MIDI ports.
internal static class LoopMidiInstallation
{
    internal static LoopMidiStatus Detect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return LoopMidiStatus.NotApplicable;
        }
        return Inspect(ReadInstalledProducts);
    }

    internal static LoopMidiStatus Inspect(Func<IEnumerable<string>> installedProducts)
    {
        try
        {
            return installedProducts().Any(name => name.Equals("loopMIDI", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("loopMIDI ", StringComparison.OrdinalIgnoreCase))
                ? LoopMidiStatus.Installed : LoopMidiStatus.Missing;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return LoopMidiStatus.Unknown;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadInstalledProducts()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
                if (uninstall is null) { continue; }
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var product = uninstall.OpenSubKey(name, writable: false);
                    if (product?.GetValue("DisplayName") is string displayName) { yield return displayName; }
                }
            }
        }
    }
}
#endif
