using System.Text.Json;

namespace PresetSender;

internal sealed class SenderSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string? InputId { get; set; }
    public string? OutputName { get; set; }
    public int OutputOccurrence { get; set; }
    public bool? VirtualOutput { get; set; }
    public int Offset { get; set; }
    public int OutputChannel { get; set; } = 1;
    public int InputChannel { get; set; }
    public bool ForwardMidi { get; set; }
    public List<MidiBinding> Bindings { get; set; } = [];
    public bool AutoSend { get; set; }
    public int AutoSendDigits { get; set; } = 3;
    public int AutoSendDelayMs { get; set; } = 35;
    public bool KeyboardEntryEnabled { get; set; } = true;
    public bool MidiEntryEnabled { get; set; } = true;
    public Dictionary<int, string> MidiNoteMap { get; set; } = MidiNoteMapping.Defaults();
    public Dictionary<string, int> NoteInputChannels { get; set; } = [];
    public List<ThruInputSettings> ThruInputs { get; set; } = [];

    internal SenderSettings Clone() => FromJson(JsonSerializer.Serialize(this));

    internal void CopyFrom(SenderSettings source)
    {
        InputId = source.InputId; OutputName = source.OutputName; OutputOccurrence = source.OutputOccurrence; VirtualOutput = source.VirtualOutput;
        Offset = source.Offset; OutputChannel = source.OutputChannel; InputChannel = source.InputChannel; ForwardMidi = source.ForwardMidi;
        AutoSend = source.AutoSend; AutoSendDigits = source.AutoSendDigits; AutoSendDelayMs = source.AutoSendDelayMs;
        KeyboardEntryEnabled = source.KeyboardEntryEnabled; MidiEntryEnabled = source.MidiEntryEnabled;
        Bindings = [.. source.Bindings]; MidiNoteMap = new(source.MidiNoteMap); NoteInputChannels = new(source.NoteInputChannels); ThruInputs = [.. source.ThruInputs];
    }

    internal static string ResolveDirectory(string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PresetSender");
        }
        if (!Path.IsPathFullyQualified(configuredDirectory))
        {
            throw new ArgumentException("PRESET_SENDER_DATA_DIRECTORY must be an absolute path.");
        }
        return Path.GetFullPath(configuredDirectory);
    }

    internal static string? ConfiguredDirectory => Environment.GetEnvironmentVariable("PRESET_SENDER_DATA_DIRECTORY");
    private static string FilePath => Path.Combine(ResolveDirectory(ConfiguredDirectory), "settings.json");

    public static SenderSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal static SenderSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<SenderSettings>(json) ?? new();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.TryGetProperty(nameof(ForwardMidi), out _)
            && document.RootElement.TryGetProperty("ForwardProgramChanges", out var legacyForward))
        { settings.ForwardMidi = legacyForward.ValueKind == JsonValueKind.True; }
        settings.Offset = settings.Offset == 1 ? 1 : 0;
        settings.OutputChannel = Math.Clamp(settings.OutputChannel, 0, 16);
        settings.InputChannel = Math.Clamp(settings.InputChannel, 0, 16);
        settings.AutoSendDelayMs = Math.Clamp(settings.AutoSendDelayMs, 10, 2000);
        settings.AutoSendDigits = Math.Clamp(settings.AutoSendDigits, 1, 4);
        settings.MidiNoteMap ??= MidiNoteMapping.Defaults();
        if (settings.MidiNoteMap.Count != 15 || settings.MidiNoteMap.Any(p => p.Key is < 0 or > 127 || MidiNoteMapping.Normalize(p.Value) is null)
            || settings.MidiNoteMap.Values.Select(MidiNoteMapping.Normalize).Distinct().Count() != 15)
        { settings.MidiNoteMap = MidiNoteMapping.Defaults(); }
        else { settings.MidiNoteMap = settings.MidiNoteMap.ToDictionary(p => p.Key, p => MidiNoteMapping.Normalize(p.Value)!); }
        settings.NoteInputChannels = (settings.NoteInputChannels ?? []).ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 0, 16));
        if (settings.InputId is not null) { settings.NoteInputChannels.TryAdd(settings.InputId, settings.InputChannel); }
        settings.ThruInputs = (settings.ThruInputs ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id))
            .DistinctBy(p => p.Id).Select(p => p with { Channel = Math.Clamp(p.Channel, 0, 16) }).ToList();
        settings.Bindings ??= [];
        settings.Bindings.RemoveAll(b => b is null || b.Kind is not (0x90 or 0xb0 or 0xc0) || b.Channel is < 1 or > 16 || b.Number is < 0 or > 127
            || !(b.Command is "Send" or "Clear" || (b.Command is { Length: 1 } && char.IsAsciiDigit(b.Command[0]))));
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, FilePath, true);
    }
}

internal sealed record ThruInputSettings(string Id, string Name, int Channel = 0);
