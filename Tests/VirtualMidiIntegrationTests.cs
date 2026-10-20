#if SENDER_WINDOWS
using System.Threading.Channels;
using MightBeUseful.Midi;

namespace PresetSender.Tests;

public sealed class VirtualMidiIntegrationTests
{
    [NativeMidiFact]
    public async Task StandaloneExeExtractsBundledApiInFreshCacheAndRepairsDamagedFiles()
    {
        string executable = Environment.GetEnvironmentVariable("PRESET_SENDER_TEST_EXE")
            ?? Path.Combine(AppContext.BaseDirectory, "PresetSender.exe");
        string cache = Path.Combine(Path.GetTempPath(), "sender-standalone-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(cache, "windows-midi", "0.99.88-preview.10", "x64");
        async Task Check()
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            start.ArgumentList.Add("--midi-backend");
            start.Environment.Remove("MBU_MIDI_API_DIRECTORY");
            start.Environment["MBU_MIDI_BACKEND"] = "current";
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = cache;
            using var process = System.Diagnostics.Process.Start(start)!;
            string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("virtual loopback available", output, StringComparison.Ordinal);
            Assert.Contains(directory, output, StringComparison.Ordinal);
        }

        await Task.WhenAll(Check(), Check());
        foreach (string name in new[] { "Windows.Devices.Midi2.dll", "Windows.Devices.Midi2.pri" })
        {
            using var resource = typeof(BundledMidiApi).Assembly.GetManifestResourceStream("PresetSender.MidiApi." + name)!;
            byte[] expected = System.Security.Cryptography.SHA256.HashData(resource);
            string path = Path.Combine(directory, name);
            Assert.Equal(expected, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
            await File.WriteAllTextAsync(path, "damaged cached component");
        }
        await Check();
        foreach (string name in new[] { "Windows.Devices.Midi2.dll", "Windows.Devices.Midi2.pri" })
        {
            using var resource = typeof(BundledMidiApi).Assembly.GetManifestResourceStream("PresetSender.MidiApi." + name)!;
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(resource),
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, name))));
        }
    }

    [NativeMidiFact]
    public async Task MissingOptionalApiReportsItsPathInsteadOfClassNotRegistered()
    {
        string executable = Environment.GetEnvironmentVariable("PRESET_SENDER_TEST_EXE")
            ?? Path.Combine(AppContext.BaseDirectory, "PresetSender.exe");
        string missingDirectory = Path.Combine(Path.GetTempPath(), "missing-midi-api-" + Guid.NewGuid().ToString("N"));
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--midi-backend");
        start.Environment["MBU_MIDI_API_DIRECTORY"] = missingDirectory;
        start.Environment["MBU_MIDI_BACKEND"] = "current";
        using var process = System.Diagnostics.Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        Assert.Contains(missingDirectory, output, StringComparison.Ordinal);
        Assert.Contains("MBU_MIDI_API_DIRECTORY", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Class not registered", output, StringComparison.Ordinal);
    }

    [NativeMidiFact]
    public async Task PublishedDeviceDeliversEveryChannelToTwoLegacyReceiversAndDisappearsOnClose()
    {
        var backend = new NativeMidiBackend();
        var before = MightBeUseful.Midi.Windows.WindowsLegacyMidiBackend.GetInputDevices().Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        string? createdId = null;
        string? relayId = null;
        // Point at an EXE-only directory to validate the published bundle too.
        string? publishedExe = Environment.GetEnvironmentVariable("PRESET_SENDER_TEST_EXE");
        using (var output = await Task.Run(() => publishedExe is null
            ? backend.OpenVirtualOutput()
            : VirtualMidiHost.Open(publishedExe)))
        {
            await Until(() =>
            {
                var ports = MightBeUseful.Midi.Windows.WindowsLegacyMidiBackend.GetInputDevices();
                createdId = ports.FirstOrDefault(p => !before.Contains(p.Id) && p.Name == "Preset Sender").Id;
                relayId = ports.FirstOrDefault(p => !before.Contains(p.Id) && p.Name == "Preset Sender (Relay)").Id;
                return createdId is not null;
            });
            Assert.Null(relayId); // The shared publisher exposes exactly one public legacy port.
            using var input = MightBeUseful.Midi.Windows.WindowsLegacyMidiBackend.OpenInput(createdId!);
            using var secondInput = MightBeUseful.Midi.Windows.WindowsLegacyMidiBackend.OpenInput(createdId!);
            var received = Channel.CreateUnbounded<int>();
            var secondReceived = Channel.CreateUnbounded<int>();
            input.MessageReceived += (_, e) => received.Writer.TryWrite(e.RawMessage);
            secondInput.MessageReceived += (_, e) => secondReceived.Writer.TryWrite(e.RawMessage);
            input.Start();
            secondInput.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            output.Send(MidiMessageEncoding.ProgramChange(42, 6));
            Assert.Equal(0xc5 | (42 << 8), await received.Reader.ReadAsync(timeout.Token));
            Assert.Equal(0xc5 | (42 << 8), await secondReceived.Reader.ReadAsync(timeout.Token));
            for (int channel = 1; channel <= 16; channel++)
            {
                output.Send(MidiMessageEncoding.ProgramChange(127, channel));
            }
            for (int channel = 1; channel <= 16; channel++)
            {
                int expected = 0xc0 | (channel - 1) | (127 << 8);
                Assert.Equal(expected, await received.Reader.ReadAsync(timeout.Token));
                Assert.Equal(expected, await secondReceived.Reader.ReadAsync(timeout.Token));
            }
            // Cover every channel-message type used by incoming MIDI forwarding.
            foreach (int message in new[] { 0x92 | (60 << 8) | (100 << 16), 0xb8 | (20 << 8) | (64 << 16),
                0xa2 | (60 << 8) | (80 << 16), 0xd2 | (90 << 8), 0xe2 | (127 << 8) | (64 << 16), 0x82 | (60 << 8) })
            {
                output.Send(message);
                Assert.Equal(message, await received.Reader.ReadAsync(timeout.Token));
                Assert.Equal(message, await secondReceived.Reader.ReadAsync(timeout.Token));
            }
            input.Stop();
            secondInput.Stop();
        }
        await Until(() => MightBeUseful.Midi.Windows.WindowsLegacyMidiBackend.GetInputDevices().All(p => p.Id != createdId && p.Id != relayId));
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) { await Task.Delay(100, timeout.Token); }
    }

    private sealed class NativeMidiFactAttribute : FactAttribute
    {
        public NativeMidiFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PRESET_SENDER_NATIVE_MIDI_TEST") != "1")
            {
                Skip = "Opt in with PRESET_SENDER_NATIVE_MIDI_TEST=1 on Windows 11 25H2+ with Windows MIDI Services.";
            }
        }
    }
}
#endif
