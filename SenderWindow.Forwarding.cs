using MightBeUseful.Midi;

namespace PresetSender;

public sealed partial class SenderWindow
{
    private readonly InputRouting _mainRouting = new();

    private sealed class InputRouting
    {
        internal HashSet<(int Kind, int Channel, int Number)> Consumed { get; } = [];
        internal HashSet<(int Channel, int Note)> ForwardedNotes { get; } = [];
    }

    private void RouteIncomingMidi(InputRouting routing, int raw, int noteChannel, bool mainInput, bool forward)
    {
        int status = raw & 0xff;
        // Only MIDI channel messages: exclude SysEx, system common, clock and transport.
        if (status is < 0x80 or >= 0xf0) { return; }
        int kind = status & 0xf0, channel = (status & 0xf) + 1, number = (raw >> 8) & 0x7f, value = (raw >> 16) & 0x7f;
        bool note = kind is 0x80 or 0x90 or 0xa0;
        var key = (Kind: note ? 0x90 : kind, Channel: channel, Number: number);
        bool release = kind == 0x80 || (kind is 0x90 or 0xb0 && value == 0);
        string? command = null;
        MidiBinding? pressed = null;
        if (mainInput) { command = _state.Receive(raw, out pressed); }

        if (mainInput && _learning is not null && pressed is not null)
        {
            ConsumeControl(routing, raw, key, release);
            _state.Bindings.RemoveAll(b => b.Command == _learning || (b.Kind == pressed.Kind && b.Channel == pressed.Channel && b.Number == pressed.Number));
            _state.Bindings.Add(pressed with { Command = _learning });
            CancelLearn(); return;
        }

        bool eligible = noteChannel == 0 || noteChannel == channel;
        bool learned = mainInput && eligible && (!note || _settings.MidiEntryEnabled)
            && _state.Bindings.Any(b => b.Kind == key.Kind && b.Channel == channel && b.Number == number);
        string? noteCommand = null;
        bool mappedNote = note && eligible && _settings.MidiEntryEnabled && !learned && _settings.MidiNoteMap.TryGetValue(number, out noteCommand);
        if (learned || mappedNote || routing.Consumed.Contains(key))
        {
            ConsumeControl(routing, raw, key, release);
            if (learned && command is not null) { Execute(command); }
            else if (mappedNote && kind == 0x90 && value > 0) { Execute(noteCommand!); }
            return;
        }

        if (!forward || _outputPort is null) { return; }
        _outputPort.Send(raw); LogMidi("Forward", raw);
        var held = (Channel: channel, Note: number);
        if (kind == 0x90 && value > 0) { routing.ForwardedNotes.Add(held); }
        else if (kind == 0x80 || kind == 0x90) { routing.ForwardedNotes.Remove(held); }
    }

    private void ConsumeControl(InputRouting routing, int raw, (int Kind, int Channel, int Number) key, bool release)
    {
        if (release)
        {
            routing.Consumed.Remove(key);
            if (key.Kind == 0x90 && routing.ForwardedNotes.Remove((key.Channel, key.Number)))
            {
                // Its note-on passed through before the mapping was changed.
                _outputPort?.Send(raw); LogMidi("Release", raw);
            }
            return;
        }
        int kind = raw & 0xf0;
        if (kind is not (0x90 or 0xb0)) { return; }
        routing.Consumed.Add(key);
        // A control can become mapped/learned after its earlier note-on was forwarded.
        if (kind == 0x90 && routing.ForwardedNotes.Remove((key.Channel, key.Number)))
        {
            int noteOff = 0x80 | (key.Channel - 1) | (key.Number << 8);
            _outputPort?.Send(noteOff); LogMidi("Release", noteOff);
        }
    }

    private static void ReleaseForwardedNotes(InputRouting routing, IMidiOutputPort? output)
    {
        foreach (var held in routing.ForwardedNotes)
        {
            try { output?.Send(0x80 | (held.Channel - 1) | (held.Note << 8)); } catch (Exception) { }
        }
        routing.ForwardedNotes.Clear();
    }
}
