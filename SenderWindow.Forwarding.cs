using MightBeUseful.Midi;

namespace PresetSender;

public sealed partial class SenderWindow
{
    private readonly InputRouting _mainRouting = new();
    private readonly Dictionary<int, long> _recentOutput = [];
    private long _outputSweep;

    private sealed class InputRouting
    {
        internal HashSet<(int Kind, int Channel, int Number)> Consumed { get; } = [];
        internal HashSet<(int Channel, int Note)> ForwardedNotes { get; } = [];
        internal HashSet<int> SustainedChannels { get; } = [];
        internal Dictionary<int, (long Start, int Count)> Echoes { get; } = [];
        internal long EchoWindow { get; set; }
        internal int EchoCount { get; set; }
    }

    private void RouteIncomingMidi(InputRouting routing, int raw, int noteChannel, bool mainInput, bool forward)
    {
        int status = raw & 0xff;
        // Only MIDI channel messages: exclude SysEx, system common, clock and transport.
        if (status is < 0x80 or >= 0xf0) { return; }
        if (FeedbackDetected(routing, raw))
        {
            Disconnect();
            _status.Text = "Possible MIDI feedback loop: disconnected. Remove the route from MIDI Out back to an input before reconnecting.";
            return;
        }
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
        SendOutput(_outputPort, raw); LogMidi("Forward", raw);
        var held = (Channel: channel, Note: number);
        if (kind == 0x90 && value > 0) { routing.ForwardedNotes.Add(held); }
        else if (kind == 0x80 || kind == 0x90) { routing.ForwardedNotes.Remove(held); }
        if (kind == 0xb0 && number == 64)
        {
            if (value >= 64) { routing.SustainedChannels.Add(channel); }
            else { routing.SustainedChannels.Remove(channel); }
        }
        else if (kind == 0xb0 && number == 121) { routing.SustainedChannels.Remove(channel); }
    }

    private void ConsumeControl(InputRouting routing, int raw, (int Kind, int Channel, int Number) key, bool release)
    {
        // A pedal can become a learned control while its forwarded sustain is on.
        if (key.Kind == 0xb0 && key.Number == 64 && routing.SustainedChannels.Remove(key.Channel))
        {
            ReleaseSustain(key.Channel, _outputPort);
        }
        if (release)
        {
            routing.Consumed.Remove(key);
            if (key.Kind == 0x90 && routing.ForwardedNotes.Remove((key.Channel, key.Number)))
            {
                // Its note-on passed through before the mapping was changed.
                SendOutput(_outputPort, raw); LogMidi("Release", raw);
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
            SendOutput(_outputPort, noteOff); LogMidi("Release", noteOff);
        }
    }

    private void ReleaseForwardedNotes(InputRouting routing, IMidiOutputPort? output)
    {
        foreach (var held in routing.ForwardedNotes)
        {
            try { SendOutput(output, 0x80 | (held.Channel - 1) | (held.Note << 8)); } catch (Exception) { }
        }
        routing.ForwardedNotes.Clear();
        int[] sustained = [.. routing.SustainedChannels];
        routing.SustainedChannels.Clear();
        foreach (int channel in sustained)
        {
            try { ReleaseSustain(channel, output); } catch (Exception) { }
        }
        routing.Echoes.Clear();
        routing.EchoCount = 0;
    }

    private void ReleaseSustain(int channel, IMidiOutputPort? output)
    {
        // Do not lift another active input's pedal on the same shared channel.
        if (_mainRouting.SustainedChannels.Contains(channel)
            || _thruPorts.Values.Any(port => port.Routing.SustainedChannels.Contains(channel))) { return; }
        SendOutput(output, 0xb0 | (channel - 1) | (64 << 8));
    }

    private void SendOutput(IMidiOutputPort? output, int raw)
    {
        if (output is null) { return; }
        long now = Environment.TickCount64;
        if (now - _outputSweep >= 1000)
        {
            foreach (int key in _recentOutput.Where(pair => now - pair.Value >= 1000).Select(pair => pair.Key).ToArray()) { _recentOutput.Remove(key); }
            _outputSweep = now;
        }
        if (_recentOutput.Count >= 4096) { _recentOutput.Clear(); }
        _recentOutput[CanonicalMessage(raw)] = now;
        output.Send(raw);
    }

    private static int CanonicalMessage(int raw) => raw & ((raw & 0xf0) is 0xc0 or 0xd0 ? 0xffff : 0xffffff);

    private bool FeedbackDetected(InputRouting routing, int raw)
    {
        // MIDI 1.0 has no origin/hop tag: isolated echoes are indistinguishable
        // from legitimate repeated input. Trip only on a rapid repeating return
        // of the SAME recently transmitted message, before entry/learning too.
        long now = Environment.TickCount64;
        int message = CanonicalMessage(raw);
        if (!_recentOutput.TryGetValue(message, out long sent) || now - sent >= 1000) { return false; }
        if (now - routing.EchoWindow >= 1000) { routing.EchoWindow = now; routing.EchoCount = 0; }
        // Also bound loops that circulate a sequence (e.g. a broadcast), rather
        // than one identical packet. Neither budget is a filter on ordinary input.
        routing.EchoCount++;
        if (!routing.Echoes.TryGetValue(message, out var echo) || now - echo.Start >= 1000) { echo = (now, 0); }
        if (routing.Echoes.Count >= 4096) { routing.Echoes.Clear(); }
        routing.Echoes[message] = (echo.Start, echo.Count + 1);
        return echo.Count + 1 >= 128 || routing.EchoCount >= 256;
    }
}
