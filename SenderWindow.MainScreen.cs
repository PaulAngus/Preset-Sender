using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace PresetSender;

public sealed partial class SenderWindow
{
    private readonly Button _configure = new() { Name = "OpenConfiguration", Content = "Configure", MinHeight = 32, Padding = new Thickness(10, 4) };
    private readonly TextBlock _connectionStatus = Text("Not connected");
    private readonly Control _configurationContent;
    private Window? _configurationWindow;
    private SenderSettings? _configurationSnapshot;
    private bool _restoringConfiguration;
    private static readonly BoxShadows Elevation = new(new BoxShadow { OffsetY = 1, Blur = 3, Color = Color.Parse("#180E1A25") });
    private static FontFamily DisplayFont => OperatingSystem.IsWindows() ? new FontFamily("Bahnschrift") : FontFamily.Default;
    private static FontFamily CommandFont => OperatingSystem.IsWindows() ? new FontFamily("Segoe UI Variable") : FontFamily.Default;

    private Control BuildMainScreen()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("44,12,*"), Margin = new Thickness(24, 20, 24, 0), HorizontalAlignment = HorizontalAlignment.Center };
        header.Bind(WidthProperty, _sender.GetObservable(WidthProperty));
        var logo = new Image { Source = BrandAssets.Logo, Width = 44, Height = 44, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(logo, AccessibilityView.Raw);
        header.Children.Add(logo);
        var title = new TextBlock { Text = "Preset Sender", FontSize = 28, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 2); header.Children.Add(title);
        AutomationProperties.SetName(_configure, "Configure Preset Sender");
        _configure.Click += (_, _) => OpenConfiguration();
        _sender.Margin = new Thickness(24, 16, 24, 24); _sender.Spacing = 16; _sender.Width = 390; _sender.HorizontalAlignment = HorizontalAlignment.Center;
        _connectionStatus.FontSize = 13; _sender.Children.Insert(0, _connectionStatus);
        AutomationProperties.SetLiveSetting(_connectionStatus, AutomationLiveSetting.Polite);
        SizeChanged += (_, _) => _sender.Width = Math.Max(0, Math.Min(390, ClientSize.Width - 48));
        PropertyChanged += (_, e) => { if (e.Property == ActualThemeVariantProperty) { UpdateDisplay(); } };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header); root.Children.Add(StatusFooter());
        root.Children.Add(new ScrollViewer { Name = "SenderScroll", Content = _sender, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        return root;
    }

    // PresetMaestro: ApprovedDisplay, ApprovedKeypad and ApprovedKey.
    private Control BuildMaestroDisplay()
    {
        _display.FontFamily = DisplayFont; _display.FontSize = 36; _display.FontWeight = FontWeight.Bold;
        _display.HorizontalAlignment = HorizontalAlignment.Left; _display.VerticalAlignment = VerticalAlignment.Center; _display.LetterSpacing = 2;
        var card = new Border
        {
            Name = "PresetDisplayCard",
            Height = 170,
            Padding = new Thickness(20),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            BoxShadow = Elevation,
            Child = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Children = { _display } }
        };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("SenderInset")); card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SenderAccent")); return card;
    }

    private Control BuildMaestroKeypad(out Control keyboardTarget)
    {
        keyboardTarget = _send;
        var deck = new Border { Name = "PresetEntryCard", Padding = new Thickness(8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), BoxShadow = Elevation };
        deck.Bind(Border.BackgroundProperty, this.GetResourceObservable("SenderInset")); deck.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SenderBorder"));
        var caption = new TextBlock { Text = "PRESET ENTRY", FontSize = 10, FontWeight = FontWeight.Bold, Margin = new Thickness(5, 1, 0, 0) };
        caption.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SenderSecondary"));
        var grid = new UniformGrid { Rows = 5, Columns = 3 };
        foreach (string command in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "Clear", "0", "Send", "Prev", "Last", "Next" })
        {
            bool send = command == "Send", task = command.Length > 1;
            var button = send ? _send : new Button();
            button.Name = send ? "SendProgram" : task ? command + "Program" : "Digit" + command;
            AutomationProperties.SetAutomationId(button, button.Name);
            button.Content = command == "Clear" ? "CLR" : command.ToUpperInvariant();
            button.MinHeight = 52; button.BorderThickness = default; button.CornerRadius = new CornerRadius(6); button.Padding = default;
            button.FontFamily = task && !send ? CommandFont : DisplayFont;
            button.FontSize = send ? 16 : task ? 12 : 20; button.FontWeight = FontWeight.Bold;
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.Bind(BackgroundProperty, this.GetResourceObservable(send ? "SenderAccent" : "SenderSurface"));
            if (send) { button.Foreground = Brushes.White; }
            else { button.Bind(ForegroundProperty, this.GetResourceObservable(command == "Clear" ? "SenderDanger" : "SenderText")); }
            AutomationProperties.SetName(button, task ? command + " preset" : "Digit " + command);
            button.Click += (_, _) => Execute(command);
            var housing = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(1, 1, 1, 2), Margin = new Thickness(3), BoxShadow = Elevation, Child = button };
            housing.Bind(Border.BackgroundProperty, this.GetResourceObservable(send ? "SenderAccent" : "SenderBorder")); grid.Children.Add(housing);
            if (command == "1") { keyboardTarget = button; }
        }
        deck.Child = new StackPanel { Spacing = 7, Children = { caption, grid } }; return deck;
    }

    private Control BuildConfigurationContent(Control settings)
    {
        var header = new TextBlock { Text = "Configuration", FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(16, 12) };
        var status = Text(""); status.Bind(TextBlock.TextProperty, _status.GetObservable(TextBlock.TextProperty));
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var save = new Button { Name = "SaveConfiguration", Content = "Save", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(save, "SaveConfiguration");
        save.Bind(BackgroundProperty, this.GetResourceObservable("SenderAccent")); save.Foreground = Brushes.White;
        save.Click += (_, _) => { if (!SaveSettings(commit: true)) { return; } _configurationSnapshot = null; _status.Text = "Configuration saved."; _configurationWindow?.Close(); };
        var cancel = new Button { Name = "CancelConfiguration", Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center }; cancel.Click += (_, _) => _configurationWindow?.Close();
        AutomationProperties.SetAutomationId(cancel, "CancelConfiguration");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { save, cancel } };
        var footer = new Border { Child = new StackPanel { Spacing = 8, Children = { status, actions } }, Padding = new Thickness(16, 10) };
        var root = new DockPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Name = "ConfigurationScroll", Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, AllowAutoHide = false }); return root;
    }

    private void OpenConfiguration()
    {
        _autoSendTimer.Stop();
        if (_configurationWindow is not null) { _configurationWindow.Activate(); return; }
        CaptureSettings(); _configurationSnapshot = _settings.Clone();
        var window = new Window
        {
            Name = "ConfigurationWindow",
            Title = "Preset Sender — Configuration",
            Icon = Icon,
            Width = 1040,
            Height = 700,
            MinWidth = 400,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ActualThemeVariant,
            Content = _configurationContent
        };
        window.Bind(BackgroundProperty, this.GetResourceObservable("SenderBackground")); window.Bind(ForegroundProperty, this.GetResourceObservable("SenderText"));
        AutomationProperties.SetAutomationId(window, "ConfigurationWindow");
        _configurationWindow = window;
        window.Opened += (_, _) => ConfigurationControl<Button>("CancelConfiguration").Focus();
        window.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || window.GetVisualDescendants().OfType<ComboBox>().Any(combo => combo.IsDropDownOpen)) { return; }
            if (_learning is not null) { CancelLearn(); _learn.Focus(); }
            else { window.Close(); }
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.Closed += (_, _) =>
        {
            _mappingWindow?.Close(); _diagnosticsWindow?.Close(); CancelLearn();
            if (_configurationSnapshot is not null) { _status.Text = "Configuration changes cancelled."; RestoreConfiguration(_configurationSnapshot); }
            _configurationSnapshot = null; window.Content = null; _configurationWindow = null;
            if (!_closed) { Activate(); _configure.Focus(); }
        };
        window.Show(this);
    }

    private void RestoreConfiguration(SenderSettings original)
    {
        _autoSendTimer.Stop(); _restoringConfiguration = true;
        try
        {
            _settings.CopyFrom(original);
            // Connect/Disconnect are live actions; keep the active connection.
            if (_outputPort is null && !_connecting)
            {
                _input.SelectedItem = (_input.ItemsSource as IEnumerable<InputChoice>)?.FirstOrDefault(p => p.Id == original.InputId);
                var outputs = (_output.ItemsSource as IEnumerable<OutputChoice>)?.ToArray() ?? [];
                _output.SelectedItem = outputs.FirstOrDefault(p => original.VirtualOutput == true ? p.IsVirtual : !p.IsVirtual && p.Name == original.OutputName && p.Occurrence == original.OutputOccurrence)
                    ?? (original.OutputName is null ? outputs.FirstOrDefault() : null);
            }
            else
            {
                _settings.InputId = (_input.SelectedItem as InputChoice)?.Id;
                if (_output.SelectedItem is OutputChoice output) { _settings.OutputName = output.Name; _settings.OutputOccurrence = output.Occurrence; _settings.VirtualOutput = output.IsVirtual; }
            }
            _state.NumberingOffset = original.Offset; _state.OutputChannel = original.OutputChannel;
            _state.InputChannel = _settings.InputId == original.InputId ? original.InputChannel : _settings.InputId is string activeInputId ? original.NoteInputChannels.GetValueOrDefault(activeInputId) : 0;
            _settings.InputChannel = _state.InputChannel;
            _state.Bindings.Clear(); _state.Bindings.AddRange(original.Bindings); _state.ResetInput();
            _channel.SelectedIndex = _state.OutputChannel; _numbering.SelectedIndex = _state.NumberingOffset; _inputChannel.SelectedIndex = _state.InputChannel; _forward.IsChecked = _settings.ForwardMidi;
            ConfigurationControl<CheckBox>("AutoSend").IsChecked = _settings.AutoSend;
            ConfigurationControl<NumericUpDown>("AutoSendDigits").Value = _settings.AutoSendDigits;
            ConfigurationControl<NumericUpDown>("AutoSendDelay").Value = _settings.AutoSendDelayMs;
            ConfigurationControl<CheckBox>("KeyboardEntry").IsChecked = _settings.KeyboardEntryEnabled;
            ConfigurationControl<CheckBox>("MidiNoteEntry").IsChecked = _settings.MidiEntryEnabled;
            if (!_settings.ForwardMidi) { ReleaseForwardedNotes(_mainRouting, _outputPort); }
            foreach (string id in _thruPorts.Keys.ToArray())
            {
                if (!_settings.ThruInputs.Any(p => p.Id == id) || !_availableInputs.Any(p => p.Id == id && EligibleThru(p)))
                { if (_thruPorts.Remove(id, out var port)) { StopThruInput(port, _outputPort); } }
            }
            if (!_closed && _outputPort is not null)
            {
                try { StartSelectedThruInputs(); }
                catch (Exception ex) { _status.Text = $"Could not restore a MIDI thru input: {ex.Message}"; }
            }
        }
        finally { _restoringConfiguration = false; RebuildThruInputs(); UpdateMappings(); UpdateDisplay(); }
    }

    private T ConfigurationControl<T>(string name) where T : Control => _configurationContent.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private IBrush MainBrush(string key)
    {
        var theme = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        return Application.Current!.TryGetResource(key, theme, out var value) && value is IBrush brush ? brush : Brushes.Gray;
    }
}
