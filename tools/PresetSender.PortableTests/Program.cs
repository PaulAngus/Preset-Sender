using System.Collections.Concurrent;
using System.Text.Json;
using Xunit.Runners;

namespace PresetSender.PortableTests;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("functional" or "native"))
        {
            Console.Error.WriteLine("Use: PresetSender.PortableTests functional|native RESULT_JSON");
            return 1;
        }
        bool native = args[0] == "native";
        var rows = new ConcurrentQueue<object>();
        var errors = new ConcurrentQueue<object>();
        using var complete = new ManualResetEventSlim();
        using var runner = AssemblyRunner.WithoutAppDomain(typeof(PresetSender.Tests.TestApplication).Assembly.Location);
        ExecutionCompleteInfo? summary = null;
        runner.TestCaseFilter = test => test.TestMethod.TestClass.Class.Name.EndsWith(".VirtualMidiIntegrationTests", StringComparison.Ordinal) == native;
        runner.OnTestPassed = test => rows.Enqueue(new { name = test.TestDisplayName, status = "passed", seconds = test.ExecutionTime });
        runner.OnTestSkipped = test => rows.Enqueue(new { name = test.TestDisplayName, status = "skipped", reason = test.SkipReason });
        runner.OnTestFailed = test => rows.Enqueue(new { name = test.TestDisplayName, status = "failed", error = test.ExceptionMessage, stack = test.ExceptionStackTrace });
        runner.OnErrorMessage = error => errors.Enqueue(error);
        runner.OnExecutionComplete = result => { summary = result; complete.Set(); };
        runner.Start(new AssemblyRunnerStartOptions { Parallel = false, PreEnumerateTheories = true });
        if (!complete.Wait(TimeSpan.FromSeconds(150)))
        {
            runner.Cancel();
            Console.Error.WriteLine("Test execution timed out.");
            Environment.Exit(1); // Parent launcher also bounds the complete process tree.
        }
        SpinWait.SpinUntil(() => runner.Status == AssemblyRunnerStatus.Idle, TimeSpan.FromSeconds(5));
        bool passed = summary is not null && summary.TotalTests > 0 && summary.TestsFailed == 0 && summary.TestsSkipped == 0 && errors.IsEmpty;
        var result = new
        {
            schemaVersion = 1,
            suite = args[0],
            status = passed ? "passed" : "failed",
            total = summary?.TotalTests ?? 0,
            failed = summary?.TestsFailed ?? 0,
            skipped = summary?.TestsSkipped ?? 0,
            seconds = summary?.ExecutionTime ?? 0,
            tests = rows.ToArray(),
            errors = errors.ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
        Console.WriteLine($"{args[0]}: {result.total} tests, {result.failed} failed, {result.skipped} skipped.");
        return passed ? 0 : 1;
    }
}
