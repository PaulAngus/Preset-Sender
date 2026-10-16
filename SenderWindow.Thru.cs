using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using MightBeUseful.Midi;

namespace PresetSender;

public sealed partial class SenderWindow
{
    private InputChoice[] _availableInputs = [];
    private readonly Dictionary<string, ThruPort> _thruPorts = [];

    private sealed class ThruPort(string id, IMidiInputPort input)
    {
        internal string Id { get; } = id;
        internal IMidiInputPort Input { get; } = input;
        internal InputRouting Routing { get; } = new();
        internal EventHandler<MidiShortMessageEventArgs>? MessageHandler { get; set; }
        internal EventHandler<string>? ErrorHandler { get; set; }
    }

    private bool EligibleThru(InputChoice input) => input.Id != (_input.SelectedItem as InputChoice)?.Id
        && input.Name != (_output.SelectedItem as OutputChoice)?.Name;

    private void RebuildThruInputs()
    {
        _thruInputs.Children.Clear();
        var choices = _availableInputs.Where(EligibleThru).ToList();
        foreach (var saved in _settings.ThruInputs)
        {
            if (_availableInputs.All(p => p.Id != saved.Id)) { choices.Add(new InputChoice(saved.Id, saved.Name)); }
        }
        foreach (var input in choices)
        {
            string id = input.Id!;
            var saved = _settings.ThruInputs.FirstOrDefault(p => p.Id == id);
            bool available = _availableInputs.Any(p => p.Id == id);
            var check = new CheckBox { Name = "ThruInput", Content = available ? input.Name : input.Name + " (unavailable)", IsChecked = saved is not null, IsEnabled = !_busy && (available || saved is not null) };
            var channel = new ComboBox { Name = "ThruNoteChannel", Width = 90, ItemsSource = _inputChannel.ItemsSource, SelectedIndex = saved?.Channel ?? _settings.NoteInputChannels.GetValueOrDefault(id), IsEnabled = available && !_busy };
            AutomationProperties.SetName(check, $"Thru input {input.Name}"); AutomationProperties.SetName(channel, $"Note entry channel for {input.Name}");
            ToolTip.SetTip(channel, "Filters note entry only. MIDI thru keeps the original channels.");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,90") };
            row.Children.Add(check); Grid.SetColumn(channel, 2); row.Children.Add(channel); _thruInputs.Children.Add(row);
            channel.SelectionChanged += (_, _) =>
            {
                _settings.NoteInputChannels[id] = channel.SelectedIndex;
                int index = _settings.ThruInputs.FindIndex(p => p.Id == id);
                if (index >= 0) { _settings.ThruInputs[index] = _settings.ThruInputs[index] with { Channel = channel.SelectedIndex }; }
            };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true)
                {
                    _settings.ThruInputs.RemoveAll(p => p.Id == id);
                    _settings.ThruInputs.Add(new ThruInputSettings(id, input.Name, channel.SelectedIndex));
                    try { if (_outputPort is not null) { StartThruInput(id); } }
                    catch (Exception ex) { check.IsChecked = false; _status.Text = $"Could not open thru input {input.Name}: {ex.Message}"; }
                }
                else
                {
                    _settings.ThruInputs.RemoveAll(p => p.Id == id);
                    if (_thruPorts.Remove(id, out var port)) { StopThruInput(port, _outputPort); }
                    if (!available) { check.IsEnabled = false; }
                }
                SaveSettings();
            };
        }
        if (choices.Count == 0) { _thruInputs.Children.Add(ConfigLabel("No additional MIDI inputs", 12, true)); }
    }

    private void StartSelectedThruInputs()
    {
        foreach (var selected in _settings.ThruInputs)
        {
            var input = _availableInputs.FirstOrDefault(p => p.Id == selected.Id);
            if (input is not null && EligibleThru(input)) { StartThruInput(selected.Id); }
        }
    }

    private void StartThruInput(string id)
    {
        if (_thruPorts.ContainsKey(id)) { return; }
        var port = new ThruPort(id, _backend.OpenInput(id));
        port.MessageHandler = (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _outputPort is null || !_thruPorts.TryGetValue(id, out var current) || !ReferenceEquals(current, port)) { return; }
            try
            {
                LogMidi("Thru in", e.RawMessage);
                int filter = _settings.ThruInputs.FirstOrDefault(p => p.Id == id)?.Channel ?? 0;
                RouteIncomingMidi(port.Routing, e.RawMessage, filter, mainInput: false, forward: true);
            }
            catch (Exception ex) { Disconnect(); _status.Text = $"MIDI thru failed: {ex.Message} Reconnect the MIDI ports."; }
        });
        port.ErrorHandler = (_, error) => Dispatcher.UIThread.Post(() =>
        {
            if (_thruPorts.TryGetValue(id, out var current) && ReferenceEquals(current, port)) { Disconnect(); _status.Text = $"Thru input error: {error} Reconnect the MIDI ports."; }
        });
        port.Input.MessageReceived += port.MessageHandler; port.Input.ErrorReceived += port.ErrorHandler;
        _thruPorts.Add(id, port);
        try { port.Input.Start(); }
        catch { _thruPorts.Remove(id); StopThruInput(port, _outputPort); throw; }
    }

    private void StopThruInputs(IMidiOutputPort? output)
    {
        var ports = _thruPorts.Values.ToArray(); _thruPorts.Clear();
        foreach (var port in ports) { StopThruInput(port, output); }
    }

    private static void StopThruInput(ThruPort port, IMidiOutputPort? output)
    {
        port.Input.MessageReceived -= port.MessageHandler; port.Input.ErrorReceived -= port.ErrorHandler;
        try { port.Input.Stop(); } catch (Exception) { }
        try { port.Input.Dispose(); } catch (Exception) { }
        ReleaseForwardedNotes(port.Routing, output);
    }
}
