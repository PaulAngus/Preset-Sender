using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Backend = MightBeUseful.Midi.Windows.WindowsLegacyMidiBackend;

namespace PresetSender.Pilot;

internal static class WindowsDesktop
{
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();

    private static readonly int[] FirstMessage = [0x2ac5];
    private static readonly string[] CoverageLimits = ["Tool-launched packaged application; Explorer launch and physical device behaviour are separate checks."];
    public static void Run(Options options)
    {
        string executable = Path.GetFullPath(options.Required("exe"));
        string directory = Path.GetFullPath(options.Required("directory"));
        string profile = Path.Combine(directory, "profile");
        string port = options.Required("port");
        bool virtualOutput = options.Required("virtual") == "true";
        Directory.CreateDirectory(profile);
        if (virtualOutput && Backend.GetInputDevices().Any(p => p.Name == port))
        {
            throw new IOException($"'{port}' already exists. Close the other Sender instance before running the pilot.");
        }
        if (!virtualOutput && !port.StartsWith("PresetSender-Test", StringComparison.Ordinal))
        {
            throw new ArgumentException("Desktop pilot requires a dedicated loopback port named PresetSender-Test..., or Sender's built-in virtual output.");
        }
        File.WriteAllText(Path.Combine(profile, "settings.json"), JsonSerializer.Serialize(new
        {
            OutputName = virtualOutput ? null : port,
            VirtualOutput = virtualOutput,
            OutputChannel = 6,
            InputId = (string?)null,
            Offset = 0,
            AutoSend = false,
            AutoSendDigits = 3,
            ForwardMidi = false,
            KeyboardEntryEnabled = true,
            MidiEntryEnabled = false
        }));
        var checks = new List<object>();
        var processes = new List<Process>();
        var applications = new List<Application>();
        var applicationLogs = new List<(Task<string> Output, Task<string> Error, string Label)>();
        var cleanupErrors = new List<string>();
        using var automation = new UIA3Automation();
        Window? window = null;
        string? stopFile = null;
        Process? receiver = null;
        Process? applicationProcess = null;
        string status = "failed";
        try
        {
            Check("Packaged application starts with isolated settings", () => Launch());
            Check("Real configuration connects the test MIDI output", Connect);
            string firstTrace = StartReceiver("first");
            Check("Keypad sends exactly C5 2A after confirmation", () =>
            {
                Invoke(window!, "Digit4"); Invoke(window!, "Digit2");
                if (ReadMessages(firstTrace).Count != 0) { throw new IOException("Unexpected MIDI before confirmation."); }
                Invoke(window!, "SendProgram");
                Wait(() => ReadMessages(firstTrace).Count >= 1, "first program change");
                AssertMessages(firstTrace, FirstMessage);
            });
            Check("Configuration returns keyboard focus and all-channel send works", () =>
            {
                var configuration = OpenConfiguration();
                Find(configuration, "OutputChannel").AsComboBox().Select(0);
                Invoke(configuration, "SaveConfiguration");
                Wait(() => FindConfiguration() is null, "configuration to close");
                FocusMainWindow();
                Wait(() => Find(window!, "OpenConfiguration").Properties.HasKeyboardFocus.Value,
                    "keyboard focus to return to Configure");
                foreach (var (key, display) in new[]
                {
                    (VirtualKeyShort.KEY_1, "001"), (VirtualKeyShort.KEY_2, "012"), (VirtualKeyShort.KEY_7, "127")
                })
                {
                    RequireForeground();
                    Keyboard.Type(key);
                    Wait(() => Find(window!, "ProgramDisplay").Name == display, "keyboard entry " + display);
                }
                RequireForeground();
                Keyboard.Type(VirtualKeyShort.RETURN); // Type releases the key as well as pressing it.
                Wait(() => ReadMessages(firstTrace).Count >= 17, "all-channel program changes");
                AssertMessages(firstTrace, FirstMessage.Concat(Enumerable.Range(0, 16).Select(i => 0x7fc0 | i)).ToArray());
                Capture.Element(window!).ToFile(Path.Combine(directory, "desktop-sent.png"));
            });
            StopReceiver(); CloseApplication();
            if (virtualOutput) { Wait(() => Backend.GetInputDevices().All(p => p.Name != port), "virtual port cleanup"); }
            Check("Restart restores the saved output channel", () =>
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "settings.json")));
                if (saved.RootElement.GetProperty("OutputChannel").GetInt32() != 0) { throw new IOException("All-channel setting was not saved."); }
                Launch(); Connect();
                string secondTrace = StartReceiver("restart");
                Invoke(window!, "Digit0"); Invoke(window!, "SendProgram");
                Wait(() => ReadMessages(secondTrace).Count >= 16, "restored all-channel send");
                AssertMessages(secondTrace, Enumerable.Range(0, 16).Select(i => 0xc0 | i).ToArray());
            });
            StopReceiver(); CloseApplication();
            Check("Test MIDI endpoint is removed on exit", () =>
            {
                if (virtualOutput) { Wait(() => Backend.GetInputDevices().All(p => p.Name != port), "final virtual port cleanup"); }
                if (!File.Exists(Path.Combine(profile, "settings.json"))) { throw new IOException("Missing isolated settings file."); }
            });
            status = "passed";
        }
        catch
        {
            try { if (window?.IsAvailable == true) { Capture.Element(window).ToFile(Path.Combine(directory, "desktop-failure.png")); } }
            catch (Exception error) { File.WriteAllText(Path.Combine(directory, "capture-error.txt"), error.ToString()); }
            throw;
        }
        finally
        {
            if (stopFile is not null) { File.WriteAllText(stopFile, "stop"); }
            foreach (Process process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
                    }
                }
                catch (Exception error) { cleanupErrors.Add(error.ToString()); }
                finally { process.Dispose(); }
            }
            foreach (var application in applications) { application.Dispose(); }
            foreach (var log in applicationLogs)
            {
                try
                {
                    File.WriteAllText(Path.Combine(directory, log.Label + ".stdout.txt"), log.Output.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                    File.WriteAllText(Path.Combine(directory, log.Label + ".stderr.txt"), log.Error.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                }
                catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            }
            File.WriteAllText(Path.Combine(directory, "desktop.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status,
                port,
                virtualOutput,
                profile,
                checks,
                cleanupErrors,
                limits = CoverageLimits
            }, Program.JsonOptions));
        }

        void Check(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            Console.WriteLine("Desktop: " + name);
            try { action(); checks.Add(new { name, status = "passed", seconds = timer.Elapsed.TotalSeconds }); }
            catch (Exception error) { Console.Error.WriteLine(error); checks.Add(new { name, status = "failed", seconds = timer.Elapsed.TotalSeconds, error = error.ToString() }); throw; }
        }

        void Launch()
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PRESET_SENDER_DATA_DIRECTORY"] = profile;
            if (virtualOutput) { start.Environment["PRESET_SENDER_VIRTUAL_DEVICE_NAME"] = port; }
            else { start.Environment.Remove("PRESET_SENDER_VIRTUAL_DEVICE_NAME"); }
            applicationProcess = Process.Start(start) ?? throw new IOException("Application did not start.");
            processes.Add(applicationProcess);
            applicationLogs.Add((applicationProcess.StandardOutput.ReadToEndAsync(), applicationProcess.StandardError.ReadToEndAsync(), "app-" + applications.Count));
            var application = Application.Attach(applicationProcess.Id);
            applications.Add(application);
            Wait(() => (window = application.GetMainWindow(automation)) is not null, "main window");
            FocusMainWindow();
            _ = Find(window!, "OpenConfiguration");
        }

        void FocusMainWindow()
        {
            window!.Focus();
            if (GetForegroundWindow() != window.Properties.NativeWindowHandle.Value)
            {
                // Windows can deny SetForegroundWindow to a background agent.
                // Activate through the title bar, like a user, without invoking a keypad button.
                var titleBar = window.TitleBar ?? throw new IOException("The test window has no accessible title bar for activation.");
                titleBar.Click();
            }
            Wait(() => GetForegroundWindow() == window.Properties.NativeWindowHandle.Value,
                "test application's foreground window (desktop must be unlocked and idle)");
        }

        void RequireForeground()
        {
            if (GetForegroundWindow() != window!.Properties.NativeWindowHandle.Value)
            {
                throw new IOException("The test application lost the foreground before keyboard input. Keep the test desktop unlocked and idle.");
            }
        }

        Window OpenConfiguration()
        {
            Invoke(window!, "OpenConfiguration");
            Window? configuration = null;
            Wait(() => (configuration = FindConfiguration()) is not null,
                "configuration window");
            return configuration!;
        }

        Window? FindConfiguration() => (window!.FindFirstDescendant(cf => cf.ByAutomationId("ConfigurationWindow"))
            ?? automation.GetDesktop().FindFirstChild(cf => cf.ByProcessId(applicationProcess!.Id).And(cf.ByAutomationId("ConfigurationWindow"))))?.AsWindow();

        void Connect()
        {
            var configuration = OpenConfiguration();
            Wait(() => Find(configuration, "RefreshMidi").IsEnabled, "MIDI discovery");
            Invoke(configuration, "ConnectMidi");
            Wait(() => Find(configuration, "DisconnectMidi").IsEnabled, "MIDI connection");
            Invoke(configuration, "SaveConfiguration");
            Wait(() => FindConfiguration() is null, "configuration to close");
        }

        string StartReceiver(string label)
        {
            string trace = Path.Combine(directory, label + ".jsonl");
            string ready = Path.Combine(directory, label + ".ready");
            stopFile = Path.Combine(directory, label + ".stop");
            string host = Environment.ProcessPath ?? throw new IOException("Could not locate the MIDI probe host.");
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) { start.ArgumentList.Add(typeof(Program).Assembly.Location); }
            foreach (string argument in new[] { "receive", "--port", port, "--trace", trace, "--ready", ready, "--stop", stopFile }) { start.ArgumentList.Add(argument); }
            receiver = Process.Start(start) ?? throw new IOException("MIDI receiver did not start.");
            processes.Add(receiver);
            Wait(() =>
            {
                if (receiver.HasExited) { throw new IOException($"Receiver exited with code {receiver.ExitCode}."); }
                return File.Exists(ready);
            }, "independent MIDI receiver");
            return trace;
        }

        void StopReceiver()
        {
            if (receiver is null) { return; }
            File.WriteAllText(stopFile!, "stop");
            if (!receiver.WaitForExit(5000) || receiver.ExitCode != 0) { throw new IOException("Receiver did not close successfully."); }
            receiver = null; stopFile = null;
        }

        void CloseApplication()
        {
            window!.Close();
            if (!applicationProcess!.WaitForExit(8000)) { throw new IOException("Application did not close."); }
            if (applicationProcess.ExitCode != 0) { throw new IOException($"Application exit code {applicationProcess.ExitCode}."); }
            window = null;
        }
    }

    private static AutomationElement Find(AutomationElement root, string id)
    {
        AutomationElement? result = null;
        Wait(() => (result = root.FindFirstDescendant(cf => cf.ByAutomationId(id))) is not null, id);
        return result!;
    }

    private static void Invoke(AutomationElement root, string id) => Find(root, id).AsButton().Invoke();

    private static void Wait(Func<bool> predicate, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(25)) { throw new TimeoutException("Timed out waiting for " + description); }
            Thread.Sleep(80);
        }
    }

    private static List<int> ReadMessages(string path)
    {
        if (!File.Exists(path)) { return []; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var messages = new List<int>();
        while (reader.ReadLine() is { } line)
        {
            try { using var row = JsonDocument.Parse(line); messages.Add(row.RootElement.GetProperty("raw").GetInt32()); }
            catch (JsonException) { break; } // Writer may still be completing its last JSON line.
        }
        return messages;
    }

    private static void AssertMessages(string trace, int[] expected)
    {
        // Allow queued duplicate/unintended messages to arrive before asserting the exact sequence.
        Thread.Sleep(250);
        var actual = ReadMessages(trace);
        if (!actual.SequenceEqual(expected))
        {
            throw new IOException($"MIDI mismatch. Expected {string.Join(", ", expected.Select(Program.Hex))}; actual {string.Join(", ", actual.Select(Program.Hex))}.");
        }
    }
}
