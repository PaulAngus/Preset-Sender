using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PresetSender;

public sealed partial class SenderWindow
{
    private readonly Button _disconnect = new() { Name = "DisconnectMidi", Content = "Disconnect", IsEnabled = false };
    private readonly TextBlock _configurationStatus = Text("Not connected");
    private readonly TextBlock _virtualMidiHelp = Text(VirtualMidiHelp(false));
    private readonly CheckBox _appToAppRouting = new() { Name = "AppToAppRouting", Content = "Virtual MIDI output", IsVisible = false };
    private readonly TextBlock _appToAppHelp = Text("A software MIDI port that other applications can receive from. Select an existing port in MIDI Out.");
    private readonly Border _loopMidiWarning = new() { Name = "LoopMidiWarning", IsVisible = false, Padding = new Thickness(12), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _refreshLoopMidi = new() { Name = "RefreshLoopMidi", Content = "Refresh devices", HorizontalAlignment = HorizontalAlignment.Left };
    private LoopMidiStatus _loopMidiStatus;
    private bool _supportsVirtualOutput;
    private bool _loopMidiWarningDismissed;
    private readonly StackPanel _thruInputs = new() { Name = "ThruInputs", Spacing = 6 };
    private readonly StackPanel _midiLearningContent = new() { Name = "MidiLearningSection", Spacing = 8 };
    private readonly DispatcherTimer _autoSendTimer = new();
    private MidiMappingWindow? _mappingWindow;
    private Window? _diagnosticsWindow;
    private readonly Queue<string> _midiLog = new();
    private TextBox? _diagnosticsText;

    private static string VirtualMidiHelp(bool supportsVirtualOutput)
    {
#if SENDER_MACOS
        return "For another app on this Mac, enable the IAC Driver in Audio MIDI Setup and create a bus. Select its output here and its matching input in the receiving app.";
#else
        return supportsVirtualOutput
            ? "To send to another app, select Preset Sender (virtual device) as the MIDI output, then select its matching input in the receiving app. Requires Windows 11 25H2 or later."
            : "Select a hardware output for direct sends. For another app, select an existing virtual output and its matching input in the receiving app.";
#endif
    }

    private void UpdateAppRoutingWarning()
    {
        bool legacyWindows = _loopMidiStatus != LoopMidiStatus.NotApplicable;
        bool appToApp = _appToAppRouting.IsChecked == true;
        _appToAppRouting.IsVisible = legacyWindows;
        _appToAppHelp.IsVisible = legacyWindows;
        _loopMidiWarning.IsVisible = legacyWindows && appToApp && _loopMidiStatus == LoopMidiStatus.Missing && !_loopMidiWarningDismissed;
        _virtualMidiHelp.Text = !legacyWindows ? VirtualMidiHelp(_supportsVirtualOutput)
            : !appToApp ? "Direct hardware output does not need loopMIDI."
            : _loopMidiStatus == LoopMidiStatus.Installed ? "loopMIDI is installed. Start it and create a port, then Refresh devices. Select that output here and its matching input in the receiving app."
            : _loopMidiStatus == LoopMidiStatus.Unknown ? "Could not check whether loopMIDI is installed. Select an existing virtual output and its matching input in the receiving app, then Refresh devices."
            : "Select an existing virtual output here and its matching input in the receiving app. Other virtual MIDI drivers can also provide this connection.";
    }

    // PresetMaestro's BuildApprovedConnection and ApprovedEntryOptions, using
    // the sender's palette and the same compact native Fluent controls.
    private Control BuildSettingsBody(out Button remove)
    {
        _autoSendTimer.Tick += (_, _) => { _autoSendTimer.Stop(); if (_settings.AutoSend) { Execute("Send"); } };
        _input.Name = "MidiInput"; _output.Name = "MidiOutput"; _inputChannel.Name = "InputChannel";
        _channel.Name = "OutputChannel"; _numbering.Name = "PresetNumbering";
        _command.Name = "LearnCommand"; _learn.Name = "LearnMidi"; _connect.Name = "ConnectMidi"; _refresh.Name = "RefreshMidi";
        _forward.Name = "ForwardMidi";
        _appToAppRouting.IsChecked = _settings.AppToAppRouting;
        AutomationProperties.SetName(_appToAppRouting, "Virtual MIDI output");
        _appToAppHelp.Margin = new Thickness(0, 2, 0, 12);
        AutomationProperties.SetAutomationId(_appToAppRouting, _appToAppRouting.Name);
        _appToAppRouting.IsCheckedChanged += (_, _) =>
        {
            if (_restoringConfiguration) { return; }
            _settings.AppToAppRouting = _appToAppRouting.IsChecked == true;
            _loopMidiWarningDismissed = false;
            UpdateAppRoutingWarning();
        };
        var warningTitle = new TextBlock { Text = "loopMIDI isn’t installed", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var warningBody = Text("To send to another app on Windows 10, install loopMIDI and create a port, or select a port from another virtual MIDI driver.");
        var dismissWarning = new Button { Name = "DismissLoopMidiWarning", Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(dismissWarning, "Dismiss loopMIDI warning");
        dismissWarning.Click += (_, _) => { _loopMidiWarningDismissed = true; UpdateAppRoutingWarning(); _appToAppRouting.Focus(); };
        _refreshLoopMidi.Click += async (_, _) => await RefreshPortsAsync();
        var warningHeader = new DockPanel(); DockPanel.SetDock(dismissWarning, Dock.Right); warningHeader.Children.Add(dismissWarning); warningHeader.Children.Add(warningTitle);
        _loopMidiWarning.Child = new StackPanel { Spacing = 8, Children = { warningHeader, warningBody, _refreshLoopMidi } };
        _loopMidiWarning.Bind(Border.BackgroundProperty, this.GetResourceObservable("SenderWarningBackground"));
        foreach (var text in new[] { warningTitle, warningBody }) { text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SenderWarningText")); }
        AutomationProperties.SetName(_loopMidiWarning, "loopMIDI is not installed for app-to-app routing");
        AutomationProperties.SetLiveSetting(_loopMidiWarning, AutomationLiveSetting.Assertive);
        foreach (var control in new Control[] { _input, _output, _inputChannel, _channel, _numbering, _connect, _refresh, _disconnect })
        {
            AutomationProperties.SetAutomationId(control, control.Name);
        }
        ToolTip.SetTip(_forward, "Forward unused channel messages on their original channels. Preset-entry and learning controls, SysEx and clock/transport are excluded.");
        _channel.ItemsSource = AllOutputChannels.Concat(Enumerable.Range(1, 16).Select(i => $"Channel {i}")).ToArray(); _channel.SelectedIndex = _state.OutputChannel;
        _inputChannel.ItemsSource = AnyInputChannel.Concat(Enumerable.Range(1, 16).Select(i => i.ToString(CultureInfo.InvariantCulture))).ToArray(); _inputChannel.SelectedIndex = _state.InputChannel;
        _numbering.ItemsSource = new[] { "0–127 (zero-based)", "1–128 (one-based)" }; _numbering.SelectedIndex = _state.NumberingOffset;
        _input.SelectionChanged += (_, _) =>
        {
            if (_restoringConfiguration) { return; }
            string? id = (_input.SelectedItem as InputChoice)?.Id;
            _inputChannel.SelectedIndex = id is not null && _settings.NoteInputChannels.TryGetValue(id, out int channel) ? channel : 0;
            RebuildThruInputs();
        };
        _output.SelectionChanged += (_, _) => { if (!_restoringConfiguration) { RebuildThruInputs(); } };
        _disconnect.Click += (_, _) => { Disconnect(); _status.Text = "Disconnected."; };
        _connect.Bind(BackgroundProperty, this.GetResourceObservable("SenderAccent")); _connect.Bind(BorderBrushProperty, this.GetResourceObservable("SenderAccent")); _connect.Foreground = Brushes.White;
        var diagnostics = new Button { Content = "Diagnostics", Name = "MidiDiagnostics" }; diagnostics.Click += (_, _) => OpenDiagnostics(diagnostics);
        var routes = new StackPanel { Spacing = 8 };
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,90") };
        _input.HorizontalAlignment = HorizontalAlignment.Stretch; _inputChannel.HorizontalAlignment = HorizontalAlignment.Stretch;
        inputRow.Children.Add(_input); Grid.SetColumn(_inputChannel, 2); inputRow.Children.Add(_inputChannel);
        AutomationProperties.SetName(_inputChannel, "MIDI input note channel");
        routes.Children.Add(CompactField("MIDI In", inputRow)); AutomationProperties.SetName(_input, "MIDI In");
        routes.Children.Add(CompactField("MIDI Out", _output));
        routes.Children.Add(CompactField("Thru In(s)", _thruInputs));
        var actionButtons = new UniformGrid { Columns = 1, Rows = 4 };
        foreach (var button in new[] { _connect, _disconnect, _refresh, diagnostics }) { button.Margin = new Thickness(0, 0, 0, 8); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center; actionButtons.Children.Add(button); }
        var actions = CompactField("Actions", actionButtons);
        var connectionGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,144"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        connectionGrid.Children.Add(routes); Grid.SetColumn(actions, 2); connectionGrid.Children.Add(actions);
        connectionGrid.SizeChanged += (_, _) =>
        {
            bool narrow = connectionGrid.Bounds.Width < 520;
            connectionGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,16,144");
            Grid.SetColumn(actions, narrow ? 0 : 2); Grid.SetRow(actions, narrow ? 1 : 0);
            actions.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
            actionButtons.Columns = narrow ? 2 : 1; actionButtons.Rows = narrow ? 2 : 4;
            foreach (Control child in actionButtons.Children) { child.Margin = new Thickness(0, 0, narrow ? 8 : 0, 8); }
        };
        _configurationStatus.Margin = new Thickness(0, 2, 0, 8); _configurationStatus.FontSize = 13;
        var connection = new StackPanel { Children = { ConfigLabel("MIDI Connection", 16), _configurationStatus, _appToAppRouting, _appToAppHelp, _loopMidiWarning, connectionGrid } };

        var auto = new CheckBox { Name = "AutoSend", Content = "Auto-send", IsChecked = _settings.AutoSend };
        var digits = new CompactNumericUpDown { Name = "AutoSendDigits", Minimum = 1, Maximum = 4, Increment = 1, Value = _settings.AutoSendDigits, Width = 88, FormatString = "0", ParsingNumberStyle = NumberStyles.Integer };
        AutomationProperties.SetName(digits, "Preset entry digit count, 1 to 4");
        ToolTip.SetTip(digits, "Applies to manual entry and auto-send. Manual entry waits for Send or Enter.");
        var digitsField = CompactField("Digits", digits);
        var delay = new CompactNumericUpDown { Name = "AutoSendDelay", Minimum = 10, Maximum = 2000, Increment = 1, Value = _settings.AutoSendDelayMs, Width = 112, FormatString = "0" };
        AutomationProperties.SetName(delay, "Auto-send delay in milliseconds");
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { delay, ConfigLabel("ms", 12, true) } };
        var delayField = CompactField("Delay", delayRow);
        var autoRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,88,16,Auto"), RowDefinitions = new RowDefinitions("Auto"), Margin = new Thickness(0, 8, 0, 8) };
        auto.VerticalAlignment = VerticalAlignment.Bottom; autoRow.Children.Add(auto); Grid.SetColumn(digitsField, 2); autoRow.Children.Add(digitsField); Grid.SetColumn(delayField, 4); autoRow.Children.Add(delayField);
        autoRow.SizeChanged += (_, _) =>
        {
            bool narrow = autoRow.Bounds.Width < 440;
            autoRow.ColumnDefinitions = new ColumnDefinitions(narrow ? "88,16,*" : "*,16,88,16,Auto");
            autoRow.RowDefinitions = new RowDefinitions(narrow ? "Auto,8,Auto" : "Auto");
            Grid.SetColumnSpan(auto, narrow ? 3 : 1);
            Grid.SetColumn(digitsField, narrow ? 0 : 2); Grid.SetRow(digitsField, narrow ? 2 : 0);
            Grid.SetColumn(delayField, narrow ? 2 : 4); Grid.SetRow(delayField, narrow ? 2 : 0);
        };
        var keyboard = new CheckBox { Name = "KeyboardEntry", Content = "Keyboard entry", IsChecked = _settings.KeyboardEntryEnabled };
        var midi = new CheckBox { Name = "MidiNoteEntry", Content = "MIDI note entry", IsChecked = _settings.MidiEntryEnabled };
        var sources = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 6, 0, 8) };
        sources.Children.Add(keyboard); Grid.SetColumn(midi, 1); sources.Children.Add(midi);
        var configureMap = new Button { Name = "ConfigureNoteMapping", Content = "Configure…", MinWidth = 100 };
        configureMap.Click += (_, _) => OpenNoteMapping(configureMap);
        var mapRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ConfigLabel("MIDI note mapping", 12, true), configureMap } };
        var entry = new StackPanel { Children = { ConfigLabel("Entry Options", 16), ConfigLabel("Keyboard and MIDI entry behaviour", 12, true), autoRow, ConfigDivider(), ConfigLabel("Entry sources", 12, true), sources, mapRow } };
        auto.IsCheckedChanged += (_, _) => { if (_restoringConfiguration) { return; } _autoSendTimer.Stop(); _settings.AutoSend = auto.IsChecked == true; };
        digits.ValueChanged += (_, _) => { if (_restoringConfiguration) { return; } _autoSendTimer.Stop(); _settings.AutoSendDigits = Math.Clamp((int)(digits.Value ?? 3), 1, 4); UpdateDisplay(); };
        delay.ValueChanged += (_, _) => { if (_restoringConfiguration) { return; } _autoSendTimer.Stop(); _settings.AutoSendDelayMs = (int)(delay.Value ?? 35); };
        keyboard.IsCheckedChanged += (_, _) => { if (_restoringConfiguration) { return; } _autoSendTimer.Stop(); _settings.KeyboardEntryEnabled = keyboard.IsChecked == true; };
        midi.IsCheckedChanged += (_, _) => { if (_restoringConfiguration) { return; } _autoSendTimer.Stop(); _settings.MidiEntryEnabled = midi.IsChecked == true; _state.ResetInput(); };

        var advanced = new StackPanel { Spacing = 12 };
        advanced.Children.Add(ConfigLabel("Additional options", 16));
        advanced.Children.Add(Field("Output channel", _channel)); advanced.Children.Add(Field("Preset numbering", _numbering));
        advanced.Children.Add(Text("For presets generated here, All channels sends the same program change on channels 1–16. A receiver listening to all channels may receive it 16 times.")); advanced.Children.Add(_last);
        _forward.IsChecked = _settings.ForwardMidi; advanced.Children.Add(_forward);
        _command.ItemsSource = Enumerable.Range(0, 10).Select(i => i.ToString(CultureInfo.InvariantCulture)).Concat(["Send", "Clear"]).ToArray(); _command.SelectedIndex = 0;
        _midiLearningContent.Children.Add(ConfigLabel("MIDI learning", 16));
        _midiLearningContent.Children.Add(Text("Learning changes are previewed immediately. Save configuration to keep them."));
        _midiLearningContent.Children.Add(Field("Command to learn", _command)); remove = new Button { Name = "RemoveLearnedMapping", Content = "Remove mapping" };
        _midiLearningContent.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _learn, remove } });
        _midiLearningContent.Children.Add(Text("Choose a command, click Learn, then send a note, CC, or program change. Release learned note/CC buttons between presses. Learned controls override the fixed note map for the matching note and channel."));
        _midiLearningContent.Children.Add(_mappingStatus);
        AutomationProperties.SetName(_mappingStatus, "MIDI learning status and assignments");
        AutomationProperties.SetLiveSetting(_mappingStatus, AutomationLiveSetting.Polite);
        ApplyConfigurationStyles(_midiLearningContent);
        advanced.Children.Add(_virtualMidiHelp);
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(10, 4, 10, 16) };
        ApplyConfigurationStyles(body);
        body.Children.Add(ConfigLabel($"Version: {typeof(SenderWindow).Assembly.GetName().Version?.ToString(3)}", 13, true));
        var connectionCard = ConfigCard(connection); var entryCard = ConfigCard(entry); var advancedCard = ConfigCard(advanced);
        connectionCard.VerticalAlignment = entryCard.VerticalAlignment = advancedCard.VerticalAlignment = VerticalAlignment.Top;
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*"), RowDefinitions = new RowDefinitions("Auto,16,Auto,16,Auto") };
        cards.Children.Add(connectionCard); cards.Children.Add(entryCard); cards.Children.Add(advancedCard);
        Grid.SetRow(entryCard, 2); Grid.SetRow(advancedCard, 4);
        bool? columns = null;
        cards.SizeChanged += (_, _) =>
        {
            bool wide = cards.Bounds.Width >= 960;
            if (columns == wide) { return; }
            columns = wide;
            cards.ColumnDefinitions = new ColumnDefinitions(wide ? "3*,16,2*" : "*");
            cards.RowDefinitions = new RowDefinitions(wide ? "Auto,16,Auto" : "Auto,16,Auto,16,Auto");
            Grid.SetColumn(advancedCard, wide ? 2 : 0); Grid.SetRow(advancedCard, wide ? 0 : 4); Grid.SetRowSpan(advancedCard, wide ? 3 : 1);
        };
        body.Children.Add(cards);
        return body;
    }

    private static void ApplyConfigurationStyles(Control body)
    {
        body.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(TemplatedControl.MinHeightProperty, 32d), new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 4)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)), new Setter(TemplatedControl.FontSizeProperty, 13d) } });
        body.Styles.Add(new Style(x => x.OfType<ComboBox>()) { Setters = { new Setter(TemplatedControl.MinHeightProperty, 32d), new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 4)), new Setter(TemplatedControl.FontSizeProperty, 13d), new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("SenderInset")), new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("SenderText")), new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("SenderBorder")) } });
        body.Styles.Add(new Style(x => x.OfType<CheckBox>()) { Setters = { new Setter(TemplatedControl.MinHeightProperty, 24d), new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TemplatedControl.FontSizeProperty, 13d) } });
        body.Styles.Add(new Style(x => x.OfType<NumericUpDown>()) { Setters = { new Setter(TemplatedControl.MinHeightProperty, 32d), new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 4)), new Setter(TemplatedControl.FontSizeProperty, 13d), new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("SenderInset")), new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("SenderText")), new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("SenderBorder")) } });
    }

    private TextBlock ConfigLabel(string text, double size = 13, bool secondary = false)
    {
        var label = Text(text); label.FontSize = size; label.VerticalAlignment = VerticalAlignment.Center;
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(secondary ? "SenderSecondary" : "SenderText")); return label;
    }
    private Control CompactField(string label, Control control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch; AutomationProperties.SetName(control, label);
        return new StackPanel { Spacing = 4, Children = { ConfigLabel(label, 12, true), control } };
    }
    private Border ConfigCard(Control child)
    {
        var card = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BoxShadow = Elevation, Child = child };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("SenderSurface")); card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SenderBorder")); return card;
    }
    private Border ConfigDivider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) }; line.Bind(Border.BackgroundProperty, this.GetResourceObservable("SenderBorder")); return line;
    }

    private void OpenNoteMapping(Button origin)
    {
        _autoSendTimer.Stop(); CancelLearn();
        if (_mappingWindow is not null) { _mappingWindow.Activate(); return; }
        var map = new MidiMappingWindow(_settings.MidiNoteMap, value =>
        {
            SaveSettings(); var previous = _settings.MidiNoteMap; _settings.MidiNoteMap = value;
            try { if (_configurationSnapshot is null) { _persistSettings(_settings); } }
            catch { _settings.MidiNoteMap = previous; throw; }
            _state.ResetInput();
        }, _midiLearningContent)
        { Icon = Icon, RequestedThemeVariant = ActualThemeVariant };
        _mappingWindow = map;
        map.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || _learning is null || map.GetVisualDescendants().OfType<ComboBox>().Any(combo => combo.IsDropDownOpen)) { return; }
            CancelLearn(); _learn.Focus(); e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        map.Closed += (_, _) => { CancelLearn(); _mappingWindow = null; if (!_closed && _configurationWindow?.IsVisible == true) { origin.Focus(); } };
        _ = map.ShowDialog(_configurationWindow!);
    }

    private void LogMidi(string source, int raw)
    {
        _midiLog.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {source}  {raw & 0xff:X2} {(raw >> 8) & 0x7f:X2} {(raw >> 16) & 0x7f:X2}");
        while (_midiLog.Count > 500) { _midiLog.Dequeue(); }
        if (_diagnosticsText is not null) { _diagnosticsText.Text = string.Join(Environment.NewLine, _midiLog); _diagnosticsText.CaretIndex = _diagnosticsText.Text.Length; }
    }

    private void OpenDiagnostics(Button origin)
    {
        if (_diagnosticsWindow is not null) { _diagnosticsWindow.Activate(); return; }
        var close = new Button { Content = "Close", Name = "CloseDiagnostics" };
        var clear = new Button { Content = "Clear log" }; clear.Click += (_, _) => { _midiLog.Clear(); if (_diagnosticsText is not null) { _diagnosticsText.Text = ""; } };
        var header = new DockPanel { Margin = new Thickness(12) }; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(new TextBlock { Text = "MIDI Diagnostics", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        var footer = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { Text("Recent MIDI activity (up to 500 messages). Bytes are hexadecimal."), clear } };
        _diagnosticsText = new TextBox { Name = "MidiActivity", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = FontFamily.Parse("Consolas, monospace"), Text = string.Join(Environment.NewLine, _midiLog), Margin = new Thickness(12, 0) };
        AutomationProperties.SetName(_diagnosticsText, "Recent MIDI messages");
        var root = new DockPanel(); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(header); root.Children.Add(footer); root.Children.Add(_diagnosticsText);
        var window = new Window { Title = "Preset Sender — MIDI Diagnostics", Icon = Icon, Width = 600, Height = 440, MinWidth = 360, MinHeight = 300, Content = root, RequestedThemeVariant = ActualThemeVariant, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Bind(BackgroundProperty, this.GetResourceObservable("SenderBackground")); window.Bind(ForegroundProperty, this.GetResourceObservable("SenderText"));
        _diagnosticsWindow = window; close.Click += (_, _) => window.Close();
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        window.Closed += (_, _) => { _diagnosticsWindow = null; _diagnosticsText = null; if (!_closed && _configurationWindow?.IsVisible == true) { origin.Focus(); } };
        window.Opened += (_, _) => close.Focus(); window.Show(_configurationWindow!);
    }
}
