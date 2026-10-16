namespace PresetSender;

internal static class MidiNoteMapping
{
    internal static readonly string[] Actions = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "Send", "Clear", "Next", "Prev", "Last"];

    internal static Dictionary<int, string> Defaults() => Actions.Select((action, index) => (action, note: 36 + index))
        .ToDictionary(pair => pair.note, pair => pair.action);

    internal static string? Normalize(string? action) => Actions.FirstOrDefault(candidate => string.Equals(candidate, action?.Trim(), StringComparison.OrdinalIgnoreCase));
}
