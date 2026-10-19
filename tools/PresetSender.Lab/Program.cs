using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PresetSender.Lab;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly string[] Limits = ["Physical MIDI devices are not tested.", "Windows desktop runs use the private packaged EXE; Explorer/Finder and signing qualification are separate.", "Mac desktop automation is not implemented."];

    private static int Main(string[] args)
    {
        try { return args.FirstOrDefault() is "pair" or "agent" ? ApplicationTestLab.PilotAgent.RunAsync(args, "preset-sender", "Preset Sender").GetAwaiter().GetResult() : Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static int Run(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "setup";
        if (mode is not ("setup" or "run" or "check" or "seal")) { throw new ArgumentException("Use setup, run, or check; seal is for the Mac development signing script."); }
        string root = Path.GetFullPath(Value("--root") ?? Path.Combine(AppContext.BaseDirectory, "..", ".."));
        string manifestPath = Path.Combine(root, "bundle-manifest.json");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions) ?? throw new IOException("Missing bundle manifest.");
        string hostRid = OperatingSystem.IsWindows() ? "win-x64" : "osx-arm64";
        if (manifest.RuntimeId != hostRid || (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture != Architecture.X64)
            || (!OperatingSystem.IsWindows() && (!OperatingSystem.IsMacOS() || RuntimeInformation.OSArchitecture != Architecture.Arm64)))
        { throw new PlatformNotSupportedException("Use the Windows x64 or Apple Silicon Mac package matching this machine."); }
        if (mode == "seal")
        {
            if (!OperatingSystem.IsMacOS()) { throw new PlatformNotSupportedException("Sign and seal the Mac bundle on a Mac development machine."); }
            foreach (var file in manifest.Files) { file.Sha256 = Hash(Contained(file.Path)); }
            manifest.Signing = "Prepared by Mac development signing script at " + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            Console.WriteLine("Mac bundle hashes updated after signing.");
            return 0;
        }
        Console.WriteLine("Verifying the portable test bundle...");
        foreach (var file in manifest.Files)
        {
            if (!File.Exists(Contained(file.Path)) || !Hash(Contained(file.Path)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("Bundle file is missing or changed: " + file.Path + ". Extract a fresh ZIP, or finish the Mac development signing step."); }
        }
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string bin = Path.Combine(root, "bin");
        string helper = Path.Combine(bin, "probe", "PresetSender.Pilot" + suffix);
        string tests = Path.Combine(bin, "tests", "PresetSender.Tests" + suffix);
        string app = Path.Combine(root, "app", "PresetSender" + suffix);
        string suite = Value("--suite") ?? (mode == "setup" ? "Functional" : "All");
        if (suite is not ("All" or "Functional" or "Native" or "Desktop")) { throw new ArgumentException("Suite must be All, Functional, Native or Desktop."); }
        string? selectedPort = Value("--loopback");
        if (selectedPort is not null && !Dedicated(selectedPort)) { throw new ArgumentException("Select a dedicated PresetSender-Test loopback or its IAC Driver-prefixed name."); }
        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        string output = Value("--result-directory") is { } requestedOutput ? Path.GetFullPath(requestedOutput) : Path.Combine(root, "results", runId);
        string resultsRoot = Path.GetFullPath(Path.Combine(root, "results")) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(resultsRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { throw new ArgumentException("Result directory must be inside this bundle's results folder."); }
        Directory.CreateDirectory(output);
        using var runLock = new FileStream(Path.Combine(root, "pilot-run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Path.Combine(output, "resource-state.json"), "{\"phase\":\"waiting\"}");
        using var hostWait = new CancellationTokenSource(TimeSpan.FromHours(2));
        using var hostLease = ApplicationTestLab.HostResourceLease.AcquireAsync(hostWait.Token).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output, "resource-state.json"), "{\"phase\":\"running\"}");
        var report = new Report { Mode = mode, Suite = suite, BundleId = manifest.BundleId, Os = OperatingSystem.IsWindows() ? (Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10") : "macOS", OsVersion = Environment.OSVersion.Version.ToString(), Limits = Limits };
        var environment = new Dictionary<string, string?>
        {
            ["PRESET_SENDER_DATA_DIRECTORY"] = Path.Combine(output, "profile"),
            ["PRESET_SENDER_SCREENSHOT_DIR"] = Path.Combine(output, "screenshots", "main"),
            ["PRESET_SENDER_CONFIG_SCREENSHOT_DIR"] = Path.Combine(output, "screenshots", "configuration"),
            ["PRESET_SENDER_NATIVE_MIDI_TEST"] = null,
            ["PRESET_SENDER_TEST_EXE"] = app,
            ["PRESET_SENDER_VIRTUAL_DEVICE_NAME"] = null,
            ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(output, "bundle-cache"),
            ["MBU_MIDI_BACKEND"] = null,
            ["MBU_MIDI_API_DIRECTORY"] = null,
            ["MIDI_FORWARDER_MIDI_API"] = null
        };
        int code = 1;
        try
        {
            JsonElement capabilities = default;
            Stage("machine-and-midi", () =>
            {
                if (OperatingSystem.IsMacOS()) { report.OsVersion = Execute("mac-version", "/usr/bin/sw_vers", ["-productVersion"]).Trim(); }
                string catalogPath = Path.Combine(output, "midi-ports.json");
                Execute("catalog", helper, ["catalog", "--result", catalogPath]);
                using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
                var inputs = catalog.RootElement.GetProperty("inputs").EnumerateArray().Select(p => p.GetString()!).ToArray();
                var outputs = catalog.RootElement.GetProperty("outputs").EnumerateArray().Select(p => p.GetString()!).ToArray();
                var candidates = inputs.Intersect(outputs, StringComparer.Ordinal).Where(Dedicated).ToArray();
                if (selectedPort is not null && !candidates.Contains(selectedPort, StringComparer.Ordinal)) { throw new IOException("Requested test loopback is not present as both input and output: " + selectedPort); }
                if (selectedPort is null && candidates.Length == 1) { selectedPort = candidates[0]; }
                if (selectedPort is null && candidates.Length > 1) { throw new IOException("Several test loopbacks exist; choose one with --loopback NAME."); }
                using var diagnostics = JsonDocument.Parse(Execute("diagnostics", app, ["--diagnostics-json"]));
                capabilities = diagnostics.RootElement.Clone();
                return new { capabilities, selectedPort, appSha256 = Hash(app), architecture = RuntimeInformation.OSArchitecture.ToString() };
            });
            if (mode != "check" && suite is "All" or "Functional") { Stage("functional", () => RunTests("functional")); }
            if (mode != "check" && suite is "All" or "Native")
            {
                if (OperatingSystem.IsWindows() && capabilities.GetProperty("canCreateVirtualPorts").GetBoolean() && selectedPort is null)
                { Stage("native-midi", () => RunTests("native")); }
                else if (selectedPort is not null)
                {
                    Stage("native-midi", () =>
                    {
                        string path = Path.Combine(output, "loopback.json");
                        Execute("loopback", helper, ["loopback", "--port", selectedPort, "--result", path]);
                        return ReadJson(path);
                    });
                }
                else { Unavailable("native-midi", PortInstructions()); }
            }
            if (mode != "check" && suite is "All" or "Desktop")
            {
                if (!OperatingSystem.IsWindows())
                {
                    if (suite == "Desktop") { Unavailable("desktop", "Mac desktop adapter is not implemented."); }
                }
                else if (!capabilities.GetProperty("canCreateVirtualPorts").GetBoolean() && selectedPort is null) { Unavailable("desktop", PortInstructions()); }
                else
                {
                    Stage("desktop", () =>
                    {
                        bool useVirtual = selectedPort is null;
                        string port = selectedPort ?? "PresetSender-Test-" + Guid.NewGuid().ToString("N")[..12];
                        string directory = Path.Combine(output, "desktop");
                        Execute("desktop", helper, ["desktop", "--exe", app, "--directory", directory, "--port", port, "--virtual", useVirtual ? "true" : "false"]);
                        return ReadJson(Path.Combine(directory, "desktop.json"));
                    });
                }
            }
            if (selectedPort is null && (!OperatingSystem.IsWindows() || !capabilities.GetProperty("canCreateVirtualPorts").GetBoolean())) { report.Notes.Add(PortInstructions()); }
            if (mode == "setup") { report.Notes.Add("Setup verified the files, port discovery and functional tests. Use Run Tests for native coverage and Windows desktop scenarios."); }
            report.Status = report.Stages.Any(s => s.Status == "unavailable") ? "incomplete" : "passed";
            code = report.Status == "passed" ? 0 : 2;
        }
        catch (Exception error) { report.Status = "failed"; report.Notes.Add(error.Message); Console.Error.WriteLine(error.Message); }
        finally
        {
            report.FinishedUtc = DateTimeOffset.UtcNow;
            string path = Path.Combine(output, "report.html");
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
            File.Copy(Path.Combine(root, "source-identity.json"), Path.Combine(output, "source-identity.json"));
            File.WriteAllText(path, Html(report, output));
            File.WriteAllText(Path.Combine(root, "LATEST-REPORT.txt"), path);
            foreach (string note in report.Notes) { Console.WriteLine(note); }
            Console.WriteLine("Report: " + path);
            if (!args.Contains("--no-open", StringComparer.Ordinal))
            {
                if (OperatingSystem.IsWindows()) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                else { Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { path }, UseShellExecute = false }); }
            }
        }
        return code;

        string? Value(string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) { return null; }
            if (index + 1 >= args.Length) { throw new ArgumentException("Missing value for " + name); }
            return args[index + 1];
        }

        string Contained(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { throw new IOException("Invalid path in bundle manifest."); }
            return path;
        }

        void Stage(string name, Func<object> action)
        {
            var stage = new StageResult { Name = name };
            report.Stages.Add(stage);
            int plannedStages = 1 + (mode != "check" && suite is "All" or "Functional" ? 1 : 0)
                + (mode != "check" && suite is "All" or "Native" ? 1 : 0)
                + (mode != "check" && (suite == "Desktop" || suite == "All" && OperatingSystem.IsWindows()) ? 1 : 0);
            string resultName = name == "native-midi" ? (selectedPort is null ? "native" : "loopback") : name;
            string progressResult = name == "desktop" ? Path.Combine(output, "desktop", "desktop.json") : Path.Combine(output, resultName + ".json");
            using var progress = new ApplicationTestLab.StageProgress(output, progressResult,
                name, report.Stages.Count, plannedStages, true);
            var timer = Stopwatch.StartNew();
            Console.WriteLine(name + ": running...");
            try { stage.Details = action(); stage.Status = "passed"; }
            catch (Exception error) { stage.Status = "failed"; stage.Details = new { error = error.Message }; throw; }
            finally { stage.Seconds = Math.Round(timer.Elapsed.TotalSeconds, 2); Console.WriteLine(name + ": " + stage.Status); }
        }

        void Unavailable(string name, string reason) => report.Stages.Add(new StageResult { Name = name, Status = "unavailable", Details = new { reason } });

        JsonElement RunTests(string name)
        {
            string result = Path.Combine(output, name + ".json");
            var extra = new Dictionary<string, string?> { ["PRESET_SENDER_NATIVE_MIDI_TEST"] = name == "native" ? "1" : null };
            Execute(name, tests, [name, result], extra);
            var data = ReadJson(result);
            if (data.GetProperty("status").GetString() != "passed" || data.GetProperty("total").GetInt32() == 0) { throw new IOException("Tests did not complete successfully."); }
            return data;
        }

        string Execute(string name, string executable, string[] arguments, Dictionary<string, string?>? extra = null)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
            foreach (var pair in environment.Concat(extra ?? []))
            {
                if (pair.Value is null) { start.Environment.Remove(pair.Key); }
                else { start.Environment[pair.Key] = pair.Value; }
            }
            using var process = Process.Start(start) ?? throw new IOException("Could not start " + name);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            bool timeout = !process.WaitForExit(180000);
            if (timeout) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            string outputText = stdout.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            string errorText = stderr.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, name + ".stdout.txt"), outputText);
            File.WriteAllText(Path.Combine(output, name + ".stderr.txt"), errorText);
            if (timeout || process.ExitCode != 0) { throw new IOException(name + " failed. See " + name + ".stderr.txt and " + name + ".stdout.txt in this report folder."); }
            return outputText;
        }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static JsonElement ReadJson(string path) { using var document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.Clone(); }
    private static bool Dedicated(string name) => name.StartsWith("PresetSender-Test", StringComparison.Ordinal) || name.StartsWith("IAC Driver PresetSender-Test", StringComparison.Ordinal);
    private static string PortInstructions() => OperatingSystem.IsWindows()
        ? "Install/start loopMIDI, add a dedicated port named PresetSender-Test, and leave loopMIDI running. See START-HERE.html."
        : "In Audio MIDI Setup, open MIDI Studio, enable IAC Driver (Device is online), add a bus named PresetSender-Test, then Run-Mac.command again. The IAC Driver prefix is detected automatically.";

    private static string Html(Report report, string output)
    {
        string Encode(string value) => WebUtility.HtmlEncode(value);
        var rows = report.Stages.Select(s => "<tr><td>" + Encode(s.Name) + "</td><td>" + Encode(s.Status) + "</td><td>" + s.Seconds.ToString(CultureInfo.InvariantCulture) + "</td><td><pre>" + Encode(JsonSerializer.Serialize(s.Details, JsonOptions)) + "</pre></td></tr>");
        var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Where(p => !p.Contains("bundle-cache", StringComparison.Ordinal) && !p.Contains(Path.DirectorySeparatorChar + "profile" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Select(p => Path.GetRelativePath(output, p).Replace('\\', '/'));
        return "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Preset Sender pilot</title><style>body{font:16px system-ui;max-width:1100px;margin:32px auto;padding:20px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd3d9;padding:10px;text-align:left;vertical-align:top}pre{white-space:pre-wrap;font-size:13px}</style><h1>Preset Sender: " + Encode(report.Status) + "</h1><p>" + Encode(report.Os + " / " + report.OsVersion + " / " + report.Suite) + "</p><table><tr><th>Stage</th><th>Status</th><th>Seconds</th><th>Details</th></tr>" + string.Concat(rows) + "</table><ul>" + string.Concat(report.Notes.Concat(report.Limits).Select(n => "<li>" + Encode(n) + "</li>")) + "</ul><h2>Evidence</h2><ul>" + string.Concat(files.Select(f => "<li><a href=\"" + Encode(f) + "\">" + Encode(f) + "</a></li>")) + "</ul></html>";
    }

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string BundleId { get; set; } = "";
        public string RuntimeId { get; set; } = "";
        public string Signing { get; set; } = "";
        public BundleFile[] Files { get; set; } = [];
    }
    private sealed class BundleFile { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; }
    private sealed class StageResult { public string Name { get; set; } = ""; public string Status { get; set; } = "running"; public double Seconds { get; set; } public object? Details { get; set; } }
    private sealed class Report
    {
        public string Mode { get; set; } = "";
        public string Suite { get; set; } = "";
        public string BundleId { get; set; } = "";
        public string Os { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string Machine { get; set; } = Environment.MachineName;
        public string Status { get; set; } = "running";
        public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset FinishedUtc { get; set; }
        public List<StageResult> Stages { get; set; } = [];
        public List<string> Notes { get; set; } = [];
        public string[] Limits { get; set; } = [];
    }
}
