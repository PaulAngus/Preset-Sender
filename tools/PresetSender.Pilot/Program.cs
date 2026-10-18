using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using MightBeUseful.Midi;
#if SENDER_WINDOWS
using Backend = MightBeUseful.Midi.Windows.WindowsLegacyMidiBackend;
#else
using Backend = MightBeUseful.Midi.CoreMidi.CoreMidiBackend;
#endif

namespace PresetSender.Pilot;

internal static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = new Options(args.Skip(1).ToArray());
            switch (args.FirstOrDefault())
            {
                case "catalog":
                    File.WriteAllText(options.Required("result"), JsonSerializer.Serialize(new
                    {
                        inputs = Backend.GetInputDevices().Select(p => p.Name).ToArray(),
                        outputs = Backend.GetOutputDevices().Select(p => p.Name).ToArray()
                    }, JsonOptions));
                    break;
                case "receive": ReceiveAsync(options).GetAwaiter().GetResult(); break;
                case "loopback": LoopbackAsync(options).GetAwaiter().GetResult(); break;
#if SENDER_WINDOWS
                case "desktop": WindowsDesktop.Run(options); break;
#endif
                default: throw new ArgumentException("Use receive, loopback, or desktop (Windows). See docs/testing-pilot.md.");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task ReceiveAsync(Options options)
    {
        string trace = options.Required("trace"), ready = options.Required("ready"), stop = options.Required("stop");
        string name = options.Required("port");
        var matches = Backend.GetInputDevices().Where(p => p.Name == name).ToArray();
        if (matches.Length != 1) { throw new IOException($"Expected one input named '{name}', found {matches.Length}."); }
        using var input = Backend.OpenInput(matches[0].Id);
        var messages = Channel.CreateUnbounded<object>();
        input.MessageReceived += (_, e) => messages.Writer.TryWrite(new { utc = DateTimeOffset.UtcNow, raw = e.RawMessage, hex = Hex(e.RawMessage) });
        input.ErrorReceived += (_, e) => messages.Writer.TryComplete(new IOException(e));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var writer = new StreamWriter(trace, append: false) { AutoFlush = true };
        input.Start();
        await File.WriteAllTextAsync(ready, JsonSerializer.Serialize(new { pid = Environment.ProcessId, port = name, utc = DateTimeOffset.UtcNow }));
        try
        {
            while (!File.Exists(stop))
            {
                while (messages.Reader.TryRead(out object? message)) { await writer.WriteLineAsync(JsonSerializer.Serialize(message)); }
                if (messages.Reader.Completion.IsFaulted) { await messages.Reader.Completion; }
                await Task.Delay(20, timeout.Token);
            }
            while (messages.Reader.TryRead(out object? message)) { await writer.WriteLineAsync(JsonSerializer.Serialize(message)); }
        }
        finally { input.Stop(); }
    }

    private static async Task LoopbackAsync(Options options)
    {
        string name = options.Required("port");
        var inputs = Backend.GetInputDevices().Where(p => p.Name == name).ToArray();
        var outputs = Backend.GetOutputDevices().Where(p => p.Name == name).ToArray();
        if (inputs.Length != 1 || outputs.Length != 1) { throw new IOException("Choose a uniquely named test loopback input/output pair."); }
        using var input = Backend.OpenInput(inputs[0].Id);
        using var output = Backend.OpenOutput(outputs[0].Index);
        var messages = Channel.CreateUnbounded<int>();
        input.MessageReceived += (_, e) => messages.Writer.TryWrite(e.RawMessage);
        input.ErrorReceived += (_, e) => messages.Writer.TryComplete(new IOException(e));
        input.Start();
        var received = new List<object>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // Literal wire vectors, independent of the application encoder.
            foreach (int message in new[] { 0x00c0, 0x2ac5, 0x7fcf, 0x643c92, 0x003c82 })
            {
                output.Send(message);
                int actual = await messages.Reader.ReadAsync(timeout.Token);
                if (actual != message) { throw new IOException($"Expected {Hex(message)}, received {Hex(actual)}."); }
                received.Add(new { utc = DateTimeOffset.UtcNow, raw = actual, hex = Hex(actual) });
            }
            await File.WriteAllTextAsync(options.Required("result"), JsonSerializer.Serialize(new { status = "passed", port = name, received }, JsonOptions));
        }
        finally { input.Stop(); }
    }

    internal static string Hex(int message)
    {
        int status = message & 0xff;
        return (status & 0xf0) is 0xc0 or 0xd0
            ? $"{status:X2} {(message >> 8) & 0x7f:X2}"
            : $"{status:X2} {(message >> 8) & 0x7f:X2} {(message >> 16) & 0x7f:X2}";
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public Options(string[] args)
    {
        if (args.Length % 2 != 0) { throw new ArgumentException("Options require --name value pairs."); }
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i][2..], args[i + 1])) { throw new ArgumentException("Invalid or repeated option."); }
        }
    }
    public string Required(string name) => values.GetValueOrDefault(name) ?? throw new ArgumentException($"Missing --{name}.");
    public string? Optional(string name) => values.GetValueOrDefault(name);
}
