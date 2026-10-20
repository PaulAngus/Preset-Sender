using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MightBeUseful.Midi;

namespace PresetSender;

public sealed partial class SenderWindow : Window
{
    private static readonly string[] AnyInputChannel = ["Omni"];
    private static readonly string[] AllOutputChannels = ["All channels"];
    private sealed record InputChoice(string? Id, string Name) { public override string ToString() => Name; }
    private sealed record OutputChoice(int Index, string Name, int Occurrence, bool IsVirtual = false) { public override string ToString() => Name; }
    private readonly SenderState _state = new();
    private readonly SenderSettings _settings;
    private readonly Action<SenderSettings> _persistSettings;
    private readonly ISenderMidiBackend _backend;
    private readonly bool _discoverPorts;
    private readonly ComboBox _input = new(), _output = new(), _channel = new(), _inputChannel = new(), _numbering = new(), _command = new();
    private readonly Button _connect = new() { Content = "Connect" }, _refresh = new() { Content = "Refresh Devices" };
    private readonly Button _send = new() { Content = "Send", Name = "SendProgram" }, _learn = new() { Content = "Learn" };
    private readonly TextBlock _display = new() { FontSize = 52, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Name = "ProgramDisplay" };
    private readonly TextBlock _status = Text("Disconnected. Select an output, then Connect."), _last = Text("No program sent yet."), _mappingStatus = Text("No MIDI controls learned.");
    private readonly CheckBox _forward = new() { Content = "Forward incoming MIDI" };
    private readonly StackPanel _sender = new() { Spacing = 10, Focusable = true, Name = "SenderKeypad" };
    private IMidiInputPort? _inputPort;
    private IMidiOutputPort? _outputPort;
    private string? _learning;
    private bool _busy, _closed, _connecting;
    private readonly Action<int>? _testSend;
    private readonly Control _keyboardTarget;

    public SenderWindow() : this(false, null) { }
    internal SenderWindow(bool offline, Action<int>? testSend, ISenderMidiBackend? backend = null, SenderSettings? settings = null, Action<SenderSettings>? persistSettings = null)
    {
        _backend = backend ?? new NativeMidiBackend();
        _discoverPorts = !offline || backend is not null;
        _testSend = testSend;
        _settings = settings ?? (offline ? new() : SenderSettings.Load());
        _persistSettings = persistSettings ?? (offline ? static _ => { } : static value => value.Save());
        _state.NumberingOffset = _settings.Offset;
        _state.OutputChannel = _settings.OutputChannel;
        _state.InputChannel = _settings.InputChannel;
        _state.Bindings.AddRange(_settings.Bindings);
        this.Bind(BackgroundProperty, this.GetResourceObservable("SenderBackground"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("SenderText"));
        Title = "Preset Sender";
        Icon = new WindowIcon(BrandAssets.Logo);
        Width = 460; Height = 740; MinWidth = 360; MinHeight = 520;
        _sender.Children.Add(BuildMaestroDisplay());
        _sender.Children.Add(BuildMaestroKeypad(out _keyboardTarget));
        AutomationProperties.SetName(_display, "Pending preset number");
        AddHandler(KeyDownEvent, SenderKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var body = BuildSettingsBody(out var remove);
        _configurationContent = BuildConfigurationContent(body);
        Content = BuildMainScreen();
        foreach (var control in new Control[] { _send, _display, _configure })
        {
            AutomationProperties.SetAutomationId(control, control.Name);
        }
        AutomationProperties.SetAutomationId(_status, "MidiStatus");
        _connect.Click += async (_, _) => await ToggleConnectionAsync();
        _refresh.Click += async (_, _) => await RefreshPortsAsync();
        _channel.SelectionChanged += (_, _) => { if (_restoringConfiguration) { return; } if (_channel.SelectedIndex >= 0) { _state.OutputChannel = _channel.SelectedIndex; } UpdateDisplay(); };
        _numbering.SelectionChanged += (_, _) => { if (_restoringConfiguration) { return; } _autoSendTimer.Stop(); _state.NumberingOffset = _numbering.SelectedIndex; _state.Clear(); UpdateDisplay(); };
        _inputChannel.SelectionChanged += (_, _) => { if (_restoringConfiguration) { return; } _state.InputChannel = _inputChannel.SelectedIndex; _state.ResetInput(); if ((_input.SelectedItem as InputChoice)?.Id is string id) { _settings.NoteInputChannels[id] = _state.InputChannel; } };
        _forward.IsCheckedChanged += (_, _) =>
        {
            if (_restoringConfiguration) { return; }
            _settings.ForwardMidi = _forward.IsChecked == true;
            if (!_settings.ForwardMidi) { ReleaseForwardedNotes(_mainRouting, _outputPort); }
        };
        _learn.Click += (_, _) =>
        {
            if (_learning is not null) { CancelLearn(); return; }
            if (_inputPort is null) { _mappingStatus.Text = _status.Text = "Connect a MIDI input before learning controls."; return; }
            _autoSendTimer.Stop();
            _learning = (string?)_command.SelectedItem;
            _state.ResetInput(); _learn.Content = "Cancel learn";
            _mappingStatus.Text = $"Waiting for a MIDI note, CC, or program change for {_learning}.";
        };
        remove.Click += (_, _) => { CancelLearn(); _state.Bindings.RemoveAll(b => b.Command == (string?)_command.SelectedItem); UpdateMappings(); };
        Opened += async (_, _) => { await RefreshPortsAsync(); _keyboardTarget.Focus(); };
        Closed += (_, _) => { _closed = true; _configurationWindow?.Close(); Disconnect(); SaveSettings(); };
        UpdateDisplay(); UpdateMappings();
    }

    private Border StatusFooter()
    {
        _configure.HorizontalAlignment = HorizontalAlignment.Right;
        var commands = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { _configure } };
        commands.Bind(WidthProperty, _sender.GetObservable(WidthProperty));
        var footer = new Border { Padding = new Thickness(20, 10), Child = new StackPanel { Spacing = 8, Children = { _status, commands } }, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseMediumLowBrush"));
        DockPanel.SetDock(footer, Dock.Bottom);
        AutomationProperties.SetName(_status, "MIDI connection and send status");
        return footer;
    }

    private static TextBlock Text(string value, HorizontalAlignment alignment = HorizontalAlignment.Stretch) => new() { Text = value, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = alignment };
    private static StackPanel Field(string label, Control control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(control, label);
        return new StackPanel { Spacing = 4, Children = { Text(label), control } };
    }

    private void SenderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Control source && TopLevel.GetTopLevel(source) != this) { return; }
        if (!_settings.KeyboardEntryEnabled) { return; }
        if (_configure.IsFocused && e.Key is Key.Enter or Key.Space) { return; }
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        int digit = e.Key >= Key.D0 && e.Key <= Key.D9 ? e.Key - Key.D0 : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
        if (digit >= 0)
        {
            Execute(digit.ToString(CultureInfo.InvariantCulture)); _keyboardTarget.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            Execute("Send");
        }
        else if (e.Key == Key.Escape) { CancelLearn(); Execute("Clear"); }
        else if (e.Key == Key.Back) { _autoSendTimer.Stop(); _state.Backspace(); UpdateDisplay(); }
        else
        {
            return;
        }

        e.Handled = true;
    }

    internal void Execute(string command)
    {
        _autoSendTimer.Stop();
        if (command.Length == 1 && char.IsAsciiDigit(command[0]))
        {
            int entryLength = _settings.AutoSendDigits;
            if (_settings.AutoSend && _state.Entry.Length >= entryLength) { _state.Clear(); }
            if (!_state.Digit(command[0] - '0', entryLength))
            {
                _status.Text = $"Entry already has {entryLength} digits. Clear or Backspace to edit.";
            }
        }
        else if (command == "Clear")
        {
            _state.Clear();
        }
        else if (command == "Send")
        {
            if (_state.TryGetProgram(out int program))
            {
                Send(program);
            }
            else
            {
                _status.Text = $"Enter a preset from {_state.NumberingOffset} to {127 + _state.NumberingOffset}.";
            }
        }
        else if (command == "Next") { Send(_state.LastProgram is int current ? (current + 1) % 128 : 0); }
        else if (command == "Prev") { Send(_state.LastProgram is int current ? (current + 127) % 128 : 127); }
        else if (command == "Last")
        {
            if (_state.PreviousProgram is int previous) { Send(previous); }
            else { _status.Text = "No previous preset to return to."; }
        }
        if (command.Length == 1 && _settings.AutoSend && _state.Entry.Length == _settings.AutoSendDigits) { _autoSendTimer.Interval = TimeSpan.FromMilliseconds(_settings.AutoSendDelayMs); _autoSendTimer.Start(); }
        UpdateDisplay();
    }

    private void Send(int program)
    {
        if (_outputPort is null && _testSend is null) { _status.Text = "Connect a MIDI output before sending."; return; }
        int sentCount = 0;
        try
        {
            int firstChannel = _state.OutputChannel == 0 ? 1 : _state.OutputChannel;
            int lastChannel = _state.OutputChannel == 0 ? 16 : _state.OutputChannel;
            for (int channel = firstChannel; channel <= lastChannel; channel++)
            {
                // Entries address bank 0. Select it on every request so a receiver's
                // previous bank (including forwarded bank changes) cannot offset the preset.
                int bankSelect = 0xb0 | (channel - 1); // CC#0, value 0, on the same channel as the PC.
                foreach (int message in new[] { bankSelect, MidiMessageEncoding.ProgramChange(program, channel) })
                {
                    if (_testSend is not null) { _testSend(message); }
                    else { SendOutput(_outputPort, message); }
                    LogMidi("Out", message);
                }
                sentCount++;
            }

            _state.Sent(program);
            string channels = _state.OutputChannel == 0 ? "All channels (1–16)" : $"Channel {_state.OutputChannel}";
            _last.Text = $"Last sent: {_state.Display(program, _settings.AutoSendDigits)} · MIDI PC {program} · {channels}";
            _status.Text = $"Sent preset {_state.Display(program, _settings.AutoSendDigits)}.";
        }
        catch (Exception ex)
        {
            Disconnect();
            string partial = sentCount > 0 ? " Some channels may already have received the change." : "";
            _status.Text = $"Send failed: {ex.Message}{partial} Reconnect the MIDI ports.";
        }
    }

    private void Receive(object? sender, MidiShortMessageEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || !ReferenceEquals(sender, _inputPort))
        {
            return;
        }

        LogMidi("In", e.RawMessage);
        try { RouteIncomingMidi(_mainRouting, e.RawMessage, _state.InputChannel, mainInput: true, forward: _settings.ForwardMidi); }
        catch (Exception ex) { Disconnect(); _status.Text = $"MIDI forwarding failed: {ex.Message} Reconnect the MIDI ports."; }
    });

    private void InputError(object? sender, string error) => Dispatcher.UIThread.Post(() =>
    {
        if (!ReferenceEquals(sender, _inputPort))
        {
            return;
        }

        Disconnect(); _status.Text = $"MIDI input error: {error} Reconnect the MIDI ports.";
    });

    private async Task RefreshPortsAsync()
    {
        if (_busy || _outputPort is not null)
        {
            return;
        }

        SetBusy(true);
        string? inputId = _input.SelectedItem is InputChoice selectedInput ? selectedInput.Id : _settings.InputId;
        string? outputName = (_output.SelectedItem as OutputChoice)?.Name ?? _settings.OutputName;
        int occurrence = (_output.SelectedItem as OutputChoice)?.Occurrence ?? _settings.OutputOccurrence;
        bool? virtualOutput = (_output.SelectedItem as OutputChoice)?.IsVirtual ?? _settings.VirtualOutput;
        try
        {
            var ports = !_discoverPorts
                ? (Inputs: Array.Empty<(string Id, string Name)>(), Outputs: Array.Empty<(int Index, string Name)>(), Virtual: false, LoopMidi: LoopMidiStatus.NotApplicable)
                : await Task.Run(() =>
                {
                    _backend.RefreshCapabilities();
                    return (Inputs: _backend.GetInputDevices().ToArray(), Outputs: _backend.GetOutputDevices().ToArray(), Virtual: _backend.SupportsVirtualOutput, LoopMidi: _backend.GetLoopMidiStatus());
                });
            if (_closed)
            {
                return;
            }

            var inputs = new[] { new InputChoice(null, "None — computer keyboard only") }.Concat(ports.Inputs.Select(p => new InputChoice(p.Id, p.Name))).ToArray();
            var outputs = ports.Outputs.Select(p => new OutputChoice(p.Index, p.Name, ports.Outputs.TakeWhile(q => q.Index != p.Index).Count(q => q.Name == p.Name))).ToList();
            virtualOutput ??= ports.Virtual && outputName is null;
            if (ports.Virtual) { outputs.Insert(0, new OutputChoice(-1, "Preset Sender (virtual device)", 0, true)); }
            _supportsVirtualOutput = ports.Virtual;
            _loopMidiStatus = ports.LoopMidi;
            _loopMidiWarningDismissed = false;
            UpdateAppRoutingWarning();
            _input.ItemsSource = inputs; _input.SelectedItem = inputs.FirstOrDefault(p => p.Id == inputId) ?? inputs[0];
            _output.ItemsSource = outputs; _output.SelectedItem = outputs.FirstOrDefault(p => virtualOutput.Value ? p.IsVirtual : !p.IsVirtual && p.Name == outputName && p.Occurrence == occurrence);
            if (_output.SelectedItem is null && outputs.Count > 0 && outputName is null && !virtualOutput.Value)
            {
                _output.SelectedIndex = 0;
            }

            _availableInputs = inputs.Where(p => p.Id is not null).ToArray(); RebuildThruInputs();
            _status.Text = outputs.Count == 0 ? "No MIDI outputs found. Connect hardware or create a virtual MIDI port, then Refresh ports." : "Disconnected. Select an output, then Connect.";
        }
        catch (Exception ex) { _status.Text = $"Could not list MIDI ports: {ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async Task ToggleConnectionAsync()
    {
        if (_busy)
        {
            return;
        }

        if (_outputPort is not null) { Disconnect(); _status.Text = "Disconnected."; return; }
        if (_output.SelectedItem is not OutputChoice output) { _status.Text = "Select a MIDI output first."; return; }
        string? inputId = (_input.SelectedItem as InputChoice)?.Id;
        _connecting = true;
        SetBusy(true);
        IMidiOutputPort? openedOutput = null;
        IMidiInputPort? openedInput = null;
        try
        {
            await Task.Run(() =>
            {
                if (output.IsVirtual) { openedOutput = _backend.OpenVirtualOutput(); }
                else
                {
                    // Device indices can change after unplugging or adding a port.
                    // Resolve the selected name again rather than opening its old slot.
                    int? currentIndex = _backend.GetOutputDevices().Where(port => port.Name == output.Name)
                        .Skip(output.Occurrence).Select(port => (int?)port.Index).FirstOrDefault();
                    if (currentIndex is null) { throw new IOException($"MIDI output {output.Name} is no longer available. Refresh devices and select an output."); }
                    openedOutput = _backend.OpenOutput(currentIndex.Value);
                }
                if (inputId is not null)
                {
                    openedInput = _backend.OpenInput(inputId);
                }
            });
            if (_closed)
            {
                return;
            }

            _outputPort = openedOutput; _inputPort = openedInput;
            if (_inputPort is not null)
            {
                _inputPort.MessageReceived += Receive; _inputPort.ErrorReceived += InputError; _inputPort.Start();
            }
            StartSelectedThruInputs();
            openedOutput = null; openedInput = null;
            _status.Text = output.IsVirtual ? "Preset Sender is available as a MIDI input in other apps. Select it there to receive program changes." : $"Connected to {output.Name}.";
            if (_configurationWindow?.IsVisible != true) { _keyboardTarget.Focus(); }
            SaveSettings();
        }
        catch (Exception ex)
        {
            StopThruInputs(openedOutput);
            // Locals retain ownership until input startup has succeeded.
            if (_inputPort is not null) { _inputPort.MessageReceived -= Receive; _inputPort.ErrorReceived -= InputError; }
            _inputPort = null; _outputPort = null;
            _status.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            _connecting = false;
            try { openedInput?.Dispose(); } catch (Exception) { }
            try { openedOutput?.Dispose(); } catch (Exception) { }
            SetBusy(false);
            UpdateDisplay();
        }
    }

    private void Disconnect()
    {
        _autoSendTimer.Stop();
        var input = _inputPort; var output = _outputPort;
        ReleaseForwardedNotes(_mainRouting, output); _mainRouting.Consumed.Clear();
        StopThruInputs(output);
        _recentOutput.Clear();
        _inputPort = null; _outputPort = null;
        if (input is not null)
        {
            input.MessageReceived -= Receive; input.ErrorReceived -= InputError;
            try { input.Stop(); } catch (Exception) { }
            try { input.Dispose(); } catch (Exception) { }
        }
        try { output?.Dispose(); } catch (Exception) { }
        _state.ResetInput(); CancelLearn();
        SetBusy(false); UpdateDisplay();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; _connect.IsEnabled = !busy && _outputPort is null;
        _disconnect.IsEnabled = !busy && _outputPort is not null;
        _input.IsEnabled = _output.IsEnabled = _refresh.IsEnabled = !busy && _outputPort is null;
        _appToAppRouting.IsEnabled = _refreshLoopMidi.IsEnabled = !busy && _outputPort is null;
        RebuildThruInputs();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        int digits = _settings.AutoSendDigits;
        _display.Text = _state.Entry.Length > 0 ? _state.Entry.PadLeft(digits, '0') : _state.LastProgram is int last ? (last + _state.NumberingOffset).ToString("D" + digits, CultureInfo.InvariantCulture) : new string('-', digits);
        _display.Foreground = MainBrush(_state.Entry.Length > 0 ? (_state.TryGetProgram(out _) ? "SenderAccent" : "SenderDanger") : _state.LastProgram.HasValue ? "SenderText" : "SenderSecondary");
        _send.IsEnabled = !_busy;
        _connectionStatus.Text = _busy ? "Connecting…" : _outputPort is null ? "Not connected" : "Connected";
        _connectionStatus.Foreground = MainBrush(_outputPort is null ? "SenderDanger" : "SenderAccent");
        _configurationStatus.Text = _connectionStatus.Text; _configurationStatus.Foreground = _connectionStatus.Foreground;
    }
    private void CancelLearn() { _learning = null; _learn.Content = "Learn"; UpdateMappings(); }
    private void UpdateMappings() => _mappingStatus.Text = _state.Bindings.Count == 0 ? "No MIDI controls learned." : string.Join(Environment.NewLine, _state.Bindings.OrderBy(b => b.Command, StringComparer.Ordinal).Select(b => $"{b.Command}: {(b.Kind == 0x90 ? "Note" : b.Kind == 0xb0 ? "CC" : "PC")} {b.Number}, channel {b.Channel}"));

    private bool SaveSettings(bool commit = false)
    {
        CaptureSettings();
        if (_configurationSnapshot is not null && !commit) { return true; }
        try { _persistSettings(_settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = $"Could not save settings: {ex.Message}"; return false; }
    }

    private void CaptureSettings()
    {
        if (_input.SelectedItem is InputChoice selectedInput) { _settings.InputId = selectedInput.Id; }
        if (_output.SelectedItem is OutputChoice selectedOutput)
        {
            _settings.OutputName = selectedOutput.Name;
            _settings.OutputOccurrence = selectedOutput.Occurrence;
            _settings.VirtualOutput = selectedOutput.IsVirtual;
        }
        _settings.Offset = _state.NumberingOffset;
        _settings.OutputChannel = _state.OutputChannel; _settings.InputChannel = _state.InputChannel;
        _settings.Bindings = _state.Bindings;
    }
}
