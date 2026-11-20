using System;
using System.Collections.Generic;
using System.Reflection;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Path = System.IO.Path;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using CodyNET.Assembler;
using CodyNET.Common.Utils;
using CodyNET.Core.Cody;
using CodyNET.Core.Devices;
using CodyNET.Frontend.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Math = System.Math;

namespace CodyNET.Frontend;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RegisterRefreshInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan FooterRefreshInterval = TimeSpan.FromMilliseconds(250);

    private ScreenControl? screen;
    private DispatcherTimer? registerRefreshTimer;
    private DispatcherTimer? footerRefreshTimer;
    private const int MemoryViewBytesPerRow = 8;
    private const int MemoryViewRowCount = 32;
    private ushort _memoryViewStartAddress;
    private readonly System.Text.StringBuilder _memoryDumpBuilder = new();
    private string? _loadedAssemblyPath;
    private static readonly long[] ClockStepFrequencies = [100_000, 500_000, 1_000_000, 2_000_000, 5_000_000, 10_000_000, -1];
    private static readonly string[] ClockStepLabels    = ["100 kHz", "500 kHz", "1 MHz", "2 MHz", "5 MHz", "10 MHz", "∞"];

    /// <summary>
    /// Highest clock frequency at which the current-line highlight is still shown while running.
    /// Above this, the PC moves too fast to follow and the highlight just flickers between random lines.
    /// </summary>
    private const long MaxCurrentLineHighlightFrequencyHz = 1_000;

    private bool _isAssemblyDirty;
    private bool _suppressCodeEditorEvents;
    private bool _suppressClockSliderEvents;
    private bool _debugUiActive;
    private int _lastClockComboIndex = 2;
    private BreakpointMargin? _breakpointMargin;
    private CurrentLineRenderer? _currentLineRenderer;
    private ErrorLineRenderer? _errorLineRenderer;
    private readonly Dictionary<int, bool> _codeEditorBreakpointLines = new();
    private Dictionary<ushort, int> _addressToSourceLine = new();
    /// <summary>Editor breakpoint line -> address the debugger breaks at for it. Only these lines are armed.</summary>
    private readonly Dictionary<int, ushort> _armedBreakpoints = new();
    /// <summary>Addresses the IDE registered with the debugger, so it can remove them again.</summary>
    private readonly HashSet<ushort> _debuggerBreakpointAddresses = new();
    private List<string> _codeLines = [];
    private FileInfo originalAssemblyFile;

    public MainWindow()
    {
        InitializeComponent();
        InitializeScreen();
        InitializePanels();
        InitializeCodeEditor();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Opened += OnOpened;

        Closed += OnWindowClosed; // Close whole application on window close

        InitUi();
    }

    private void InitializeCodeEditor()
    {
        _breakpointMargin = new BreakpointMargin(_codeEditorBreakpointLines, _armedBreakpoints.ContainsKey, ToggleBreakpoint);
        CodeEditorTextBox.TextArea.LeftMargins.Insert(0, _breakpointMargin);

        CodeEditorTextBox.Document.TextChanged += OnCodeEditorDocumentTextChanged;

        using var stream = AssetLoader.Open(new Uri("avares://CodyNET.Frontend/Assets/CodyNET.xshd"));
        using var xmlReader = XmlReader.Create(stream);
        var xshd = HighlightingLoader.LoadXshd(xmlReader);
        CodeEditorTextBox.SyntaxHighlighting = HighlightingLoader.Load(xshd, HighlightingManager.Instance);

        _errorLineRenderer = new ErrorLineRenderer();
        CodeEditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_errorLineRenderer);
        CodeEditorTextBox.PointerHover += OnCodeEditorPointerHover;
        CodeEditorTextBox.PointerHoverStopped += (_, _) => HideErrorToolTip();

        _currentLineRenderer = new CurrentLineRenderer();
        CodeEditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_currentLineRenderer);
    }

    private string _fullVersionText = "";

    private void InitUi()
    {
        _fullVersionText = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        var version = _fullVersionText.Contains('+') ? _fullVersionText[.._fullVersionText.IndexOf('+')] : _fullVersionText;
        FooterVersionText.Text = $"CodyNET - Version {version}";
        ToolTip.SetTip(FooterVersionText, _fullVersionText);
        SetCodePanelState(null, null);
    }
    
    private void OnOpened(object? sender, EventArgs e)
    {
        screen?.Focus();
        
        // Set buttons visible only if debug mode is enabled. Debug UI can also be disabled while debug mode is on
        var debugMode = FrontendHostBridge.DebugMode;
        DebugMenu.IsVisible = debugMode;
        LoadAssemblyMenuItem.IsVisible = debugMode;
        RunAssemblyMenuItem.IsVisible = debugMode;
        SetDebugUiActive(debugMode);
        RefreshRegisterValues();
        SetMemoryViewStart(0);
        RefreshBreakpointsPanel();
        SetErrorList([], NoErrorsText);
        SetRunMode(_runMode); // Sync button label, menu header and tooltip
        SyncInitialClockFrequency();
        registerRefreshTimer?.Start();
        footerRefreshTimer?.Start();
    }

    private void SyncInitialClockFrequency()
    {
        var hz = FrontendHostBridge.InitialClockFrequency;
        if (hz == 0)
            return;

        var step = Array.IndexOf(ClockStepFrequencies, hz);
        _lastClockComboIndex = step >= 0 ? step : ClockStepFrequencies.Length;
        _suppressClockSliderEvents = true;
        ClockSpeedComboBox.SelectedIndex = _lastClockComboIndex;
        _suppressClockSliderEvents = false;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        registerRefreshTimer?.Stop();
        Log.Info("Main window closed. Exiting application.");
        Environment.Exit(0);
    }
    
    private void InitializeScreen()
    {
        screen = this.FindControl<ScreenControl>("Screen");
        if (screen != null)
        {
            FrontendHostBridge.SetScreen(screen);
        }
    }

    private void InitializePanels()
    {
        registerRefreshTimer = new DispatcherTimer
        {
            Interval = RegisterRefreshInterval
        };
        footerRefreshTimer = new DispatcherTimer
        {
            Interval = FooterRefreshInterval
        };
        registerRefreshTimer.Tick += (_, _) =>
        {
            RefreshRegisterValues();
            RefreshMemoryView();
        };
        footerRefreshTimer.Tick += (_, _) => RefreshFooter();
    }

    private void RefreshMemoryView()
    {
        if (!_debugUiActive)
            return;
        if (MemoryDumpText == null || !FrontendHostBridge.HasMemoryReader)
            return;

        _memoryDumpBuilder.Clear();
        int start = _memoryViewStartAddress;
        for (int row = 0; row < MemoryViewRowCount; row++)
        {
            int rowStart = start + row * MemoryViewBytesPerRow;
            if (rowStart > 0xFFFF)
                break;

            _memoryDumpBuilder.Append(rowStart.ToString("X4"));
            _memoryDumpBuilder.Append("  ");

            // Hex bytes
            for (int col = 0; col < MemoryViewBytesPerRow; col++)
            {
                int addr = rowStart + col;
                if (addr > 0xFFFF)
                {
                    _memoryDumpBuilder.Append("   ");
                    continue;
                }
                byte value = FrontendHostBridge.ReadMemory((ushort)addr);
                _memoryDumpBuilder.Append(value.ToString("X2"));
                _memoryDumpBuilder.Append(' ');
            }

            _memoryDumpBuilder.Append(' ');

            // ASCII representation
            for (int col = 0; col < MemoryViewBytesPerRow; col++)
            {
                int addr = rowStart + col;
                if (addr > 0xFFFF)
                    break;
                byte value = FrontendHostBridge.ReadMemory((ushort)addr);
                char c = value >= 0x20 && value < 0x7F ? (char)value : '.';
                _memoryDumpBuilder.Append(c);
            }

            if (row < MemoryViewRowCount - 1)
                _memoryDumpBuilder.Append('\n');
        }

        MemoryDumpText.Text = _memoryDumpBuilder.ToString();
    }

    private void SetMemoryViewStart(int address)
    {
        address &= ~(MemoryViewBytesPerRow - 1); // align
        if (address < 0)
            address = 0;
        int maxStart = 0x10000 - MemoryViewBytesPerRow * MemoryViewRowCount;
        if (maxStart < 0)
            maxStart = 0;
        if (address > maxStart)
            address = maxStart;

        _memoryViewStartAddress = (ushort)address;
        if (MemoryAddressInput != null)
            MemoryAddressInput.Text = _memoryViewStartAddress.ToString("X4");
        RefreshMemoryView();
    }

    private void OnMemoryGoButtonClick(object? sender, RoutedEventArgs e)
    {
        JumpToMemoryInputAddress();
    }

    private void OnMemoryAddressInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            JumpToMemoryInputAddress();
            e.Handled = true;
        }
    }

    private void JumpToMemoryInputAddress()
    {
        if (MemoryAddressInput == null)
            return;
        var text = MemoryAddressInput.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(2);
        if (text.StartsWith("$"))
            text = text.Substring(1);

        if (int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int addr))
        {
            SetMemoryViewStart(addr);
        }
    }

    private void OnMemoryShortcutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (tag == "PC")
        {
            var snapshot = FrontendHostBridge.GetRegisterSnapshot();
            if (snapshot != null)
                SetMemoryViewStart(snapshot.PC);
        }
        else
        {
            var text = tag.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tag.Substring(2) : tag;
            if (int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int addr))
            {
                SetMemoryViewStart(addr);
            }
        }

        // Reset selection so the same entry can be chosen again
        combo.SelectedItem = null;
    }

    private void OnMemoryNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
            return;
        if (int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
        {
            SetMemoryViewStart(_memoryViewStartAddress + delta);
        }
    }

    private void RefreshRegisterValues()
    {
        if (!_debugUiActive)
            return;
        var snapshot = FrontendHostBridge.GetRegisterSnapshot();
        if (snapshot == null)
        {
            return;
        }

        SetText("RegisterAHexText", snapshot.A.ToString("X2"));
        SetText("RegisterADecText", snapshot.A.ToString(CultureInfo.InvariantCulture));
        SetText("RegisterXHexText", snapshot.X.ToString("X2"));
        SetText("RegisterXDecText", snapshot.X.ToString(CultureInfo.InvariantCulture));
        SetText("RegisterYHexText", snapshot.Y.ToString("X2"));
        SetText("RegisterYDecText", snapshot.Y.ToString(CultureInfo.InvariantCulture));
        SetText("RegisterSPHexText", snapshot.S.ToString("X2"));
        SetText("RegisterSPDecText", snapshot.S.ToString(CultureInfo.InvariantCulture));
        SetText("RegisterPCHexText", snapshot.PC.ToString("X4"));
        SetText("RegisterPCDecText", snapshot.PC.ToString(CultureInfo.InvariantCulture));
        SetText("RegisterPHexText", snapshot.P.ToString("X2"));
        SetText("RegisterPDecText", snapshot.P.ToString(CultureInfo.InvariantCulture));

        SetFlagText("FlagCarryText", snapshot.Carry);
        SetFlagText("FlagZeroText", snapshot.Zero);
        SetFlagText("FlagInterruptDisableText", snapshot.InterruptDisable);
        SetFlagText("FlagDecimalText", snapshot.Decimal);
        SetFlagText("FlagBreakText", snapshot.Break);
        SetFlagText("FlagOverflowText", snapshot.Overflow);
        SetFlagText("FlagNegativeText", snapshot.Negative);

        UpdateCurrentLineHighlight(snapshot.PC);
    }

    private void UpdateCurrentLineHighlight(ushort pc)
    {
        if (_currentLineRenderer is null) return;

        var status = FrontendHostBridge.GetStatusSnapshot();

        int? newLine = null;
        if (ShouldShowCurrentLine(status))
        {
            _addressToSourceLine.TryGetValue(pc, out var sourceLine);  // 0 if not found
            newLine = sourceLine > 0 ? sourceLine : null;
        }

        if (_currentLineRenderer.CurrentLine == newLine) return;   // nothing changed

        _currentLineRenderer.CurrentLine = newLine;
        CodeEditorTextBox.TextArea.TextView.InvalidateVisual();

        // Auto-scroll to the highlighted line when paused/stepping (not while running,
        // because the PC jumps around too fast and would be distracting).
        if (newLine is not null)
        {
            if (status?.RunStatus == RunStatus.Paused)
            {
                // CodeEditorTextBox.TextArea.Caret.Line   = newLine.Value;
                // CodeEditorTextBox.TextArea.Caret.Column = 1;
                // CodeEditorTextBox.TextArea.Caret.BringCaretToView();
                
                CodeEditorTextBox.ScrollToLine(newLine.Value);
            }
        }
    }

    /// <summary>
    /// The current-line highlight is only useful when the CPU is halted (paused, stepping, breakpoint)
    /// or running slowly enough to follow. At full speed it just makes random lines blink.
    /// </summary>
    private static bool ShouldShowCurrentLine(CodyStatusSnapshot? status)
    {
        if (status is null)
            return false;
        if (status.RunStatus != RunStatus.Running)
            return true;

        var targetHz = status.ProfilerSnapshot?.TargetFrequency ?? 0;
        return targetHz > 0 && targetHz <= MaxCurrentLineHighlightFrequencyHz;   // <= 0 means unlimited
    }

    /// <summary>
    /// Pushes the editor breakpoints into the emulator's debugger as address breakpoints, so they take
    /// effect immediately without re-assembling. Line -> address comes from the last assembly; a
    /// breakpoint on a line without code (label, comment, blank) breaks at the next line that has code.
    /// While the editor has changes that are not assembled yet, breakpoints that are already armed stay
    /// armed (they match the program in memory), removing or disabling one still applies right away,
    /// and new ones are armed by the next Assemble/Run.
    /// </summary>
    private void SyncBreakpointsToDebugger()
    {
        var debugger = FrontendHostBridge.Debugger;
        if (debugger is null)
        {
            _armedBreakpoints.Clear();
            return;
        }

        if (_addressToSourceLine.Count > 0) // Address map matches the editor text: re-resolve everything
        {
            var lineToAddress = new SortedDictionary<int, ushort>();
            foreach (var (address, line) in _addressToSourceLine)
            {
                if (!lineToAddress.TryGetValue(line, out var existing) || address < existing)
                    lineToAddress[line] = address;
            }
            var linesWithCode = lineToAddress.Keys.ToList();

            _armedBreakpoints.Clear();
            foreach (var line in _codeEditorBreakpointLines.Keys)
            {
                var index = linesWithCode.BinarySearch(line);
                if (index < 0)
                    index = ~index; // No code on this line: use the next line that has some
                if (index < linesWithCode.Count)
                    _armedBreakpoints[line] = lineToAddress[linesWithCode[index]];
            }
        }
        else
        {
            foreach (var line in _armedBreakpoints.Keys.Where(l => !_codeEditorBreakpointLines.ContainsKey(l)).ToList())
                _armedBreakpoints.Remove(line);
        }

        // Several lines can resolve to the same address; it breaks if any of them is enabled.
        var wanted = new Dictionary<ushort, (bool Enabled, int Line)>();
        foreach (var (line, address) in _armedBreakpoints.OrderBy(b => b.Key))
        {
            var enabled = _codeEditorBreakpointLines[line];
            wanted[address] = wanted.TryGetValue(address, out var existing)
                ? (existing.Enabled || enabled, existing.Line)
                : (enabled, line);
        }

        foreach (var address in _debuggerBreakpointAddresses.Where(a => !wanted.ContainsKey(a)).ToList())
        {
            debugger.RemoveBreakpoint(address);
            _debuggerBreakpointAddresses.Remove(address);
        }

        foreach (var (address, (enabled, line)) in wanted)
        {
            if (_debuggerBreakpointAddresses.Add(address))
                debugger.AddBreakpoint(address, enabled, $"{GetLoadedAssemblyFileName()} line {line}");
            else
                debugger.SetBreakpointEnabled(address, enabled);
        }
    }

    private void RefreshBreakpointsPanel()
    {
        SyncBreakpointsToDebugger();

        var breakpointsPanel = BreakpointsPanel;
        breakpointsPanel.Children.Clear();

        AddBreakpointHeader();

        _breakpointMargin?.InvalidateVisual();

        var count = _codeEditorBreakpointLines.Count;
        BreakpointBadge.IsVisible = count > 0;
        BreakpointBadgeText.Text = count.ToString();

        if (count == 0)
        {
            AddBreakpointPlaceholder("No breakpoints configured.");
            return;
        }

        foreach (int i in _codeEditorBreakpointLines.Keys.OrderBy(x => x))
        {
            AddBreakpointRow(i);
        }
    }

    private void RefreshFooter()
    {
        var status = FrontendHostBridge.GetStatusSnapshot();
        if (status == null)
            return;


        switch (status.RunStatus)
        {
            case RunStatus.Running:
                FooterStatusText.Text = "Running";
                FooterStatusText.Foreground = Brush.Parse("#31c436");
                ClockActualLabel.Foreground = Brush.Parse("#31c436");
                MenuPauseResumeButton.Header = "_Pause [F9]";
                MenuStepButton.IsEnabled = false;
                EmulatorPauseResumeButton.Content = "Pause [F9]";
                EmulatorStepButton.IsEnabled = false;
                break;
            case RunStatus.Paused:
                FooterStatusText.Text = "Paused";
                FooterStatusText.Foreground = Brush.Parse("#ff2414");
                ClockActualLabel.Foreground = Brush.Parse("#ff2414");
                MenuPauseResumeButton.Header = "_Resume [F9]";
                MenuStepButton.IsEnabled = true;
                EmulatorPauseResumeButton.Content = "Resume [F9]";
                EmulatorStepButton.IsEnabled = true;
                break;
        }
        
        if (status.ProfilerSnapshot == null)
            return;
        var actualFrequencyText = Unit.FormatSi(status.ProfilerSnapshot.ActualFrequency, "Hz");
        ClockActualLabel.Text = status.ProfilerSnapshot.TargetFrequency > 0
            ? $"{actualFrequencyText} ({Math.Round(status.ProfilerSnapshot.FrequencyTargetPercent)}%)"
            : actualFrequencyText;
    }

    private void SetFlagText(string controlName, bool value)
    {
        SetText(controlName, value ? "1" : "0");
    }

    private void SetText(string controlName, string text)
    {
        var textBlock = this.FindControl<TextBlock>(controlName);
        if (textBlock != null)
        {
            textBlock.Text = text;
        }
    }

    private void OnScaleMenuClick(object? sender, RoutedEventArgs e)
    {
        if (screen == null || sender is not MenuItem menuItem || menuItem.Parent is not MenuItem viewMenu)
        {
            return;
        }

        // Uncheck sibling scale items (not render mode items)
        foreach (var child in viewMenu.Items)
        {
            if (child is MenuItem sibling && sibling.ToggleType == MenuItemToggleType.CheckBox
                && sibling.Tag is string t && (t == "auto" || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                sibling.IsChecked = sibling == menuItem;
        }

        var isAuto = menuItem.Tag is string tagValue && tagValue == "auto";
        screen.FitToContainer = isAuto;
        ScreenScrollViewer.HorizontalScrollBarVisibility = isAuto
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        ScreenScrollViewer.VerticalScrollBarVisibility = isAuto
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        if (!isAuto && menuItem.Tag is string scaleValue
            && double.TryParse(scaleValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            screen.ScaleFactor = scale;
        }
    }
    
    private static readonly string[] RenderModes = ["None", "LowQuality", "MediumQuality", "HighQuality"];

    private void OnRenderModeClick(object? sender, RoutedEventArgs e)
    {
        if (screen == null || sender is not MenuItem menuItem || menuItem.Parent is not MenuItem viewMenu)
            return;

        // Uncheck sibling render mode items
        foreach (var child in viewMenu.Items)
        {
            if (child is MenuItem sibling && sibling.Tag is string t && RenderModes.Contains(t))
                sibling.IsChecked = sibling == menuItem;
        }

        if (menuItem.Tag is string modeTag && Enum.TryParse<BitmapInterpolationMode>(modeTag, out var mode))
        {
            RenderOptions.SetBitmapInterpolationMode(screen, mode);
            screen.InvalidateVisual();
        }
    }

    private async void OnLoadUart1Click(object? sender, RoutedEventArgs e)
    {
        if (screen == null || sender is not MenuItem menuItem)
        {
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Load UART1 Source",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Binary and BASIC files")
                    {
                        Patterns = ["*.bin", "*.bas"]
                    },
                    new FilePickerFileType("BASIC files")
                    {
                        Patterns = ["*.bas"]
                    },
                    new FilePickerFileType("Binary files")
                    {
                        Patterns = ["*.bin"]
                    },
                    new FilePickerFileType("All files")
                    {
                        Patterns = ["*.*"]
                    },
                ]
            });
        
        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (path == null)
            return;

        var fileInfo = new FileInfo(path);
        //UpdateCodePanel(fileInfo);
        FrontendHostBridge.LoadUartSource(fileInfo);
    }
    
    private void MessageBox(string title, string message)
    {
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await MessageBoxManager.GetMessageBoxStandard(title, message).ShowAsPopupAsync(this);
        });
    }
    
    private void OnClockMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
            return;

        if (menuItem.Tag is not string tagValue)
            return;

        if (!long.TryParse(tagValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frequencyHz))
            return;

        FrontendHostBridge.SetClockFrequency(frequencyHz);
        SyncClockSliderToFrequency(frequencyHz);
    }

    private void OnClockComboBoxChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressClockSliderEvents || ClockSpeedComboBox is null)
            return;

        var step = ClockSpeedComboBox.SelectedIndex;

        if (step < 0 || step >= ClockStepFrequencies.Length)
            return; // "Custom…" is handled by OnClockDropDownClosed

        _lastClockComboIndex = step;
        FrontendHostBridge.SetClockFrequency(ClockStepFrequencies[step]);
    }

    private void OnClockDropDownClosed(object? sender, EventArgs e)
    {
        if (ClockSpeedComboBox.SelectedIndex == ClockStepFrequencies.Length)
            _ = OpenCustomClockDialogAsync();
    }

    private async Task OpenCustomClockDialogAsync()
    {
        var dialog = new ClockFrequencyDialog();
        await dialog.ShowDialog(this);

        if (dialog.ResultHz is { } hz)
        {
            FrontendHostBridge.SetClockFrequency(hz);
            SyncClockSliderToFrequency(hz);
            // If no preset matched, keep "Custom…" selected and update the actual label
            if (Array.IndexOf(ClockStepFrequencies, hz) < 0)
                ClockActualLabel.Text = Unit.FormatSi(hz, "Hz");
        }
        else
        {
            // User cancelled - restore previous ComboBox state without touching the emulator
            _suppressClockSliderEvents = true;
            ClockSpeedComboBox.SelectedIndex = _lastClockComboIndex;
            _suppressClockSliderEvents = false;
        }
    }

    private void SyncClockSliderToFrequency(long hz)
    {
        var step = Array.IndexOf(ClockStepFrequencies, hz);
        if (step < 0)
            return;

        _suppressClockSliderEvents = true;
        ClockSpeedComboBox.SelectedIndex = step;
        _suppressClockSliderEvents = false;
    }

    private void OnPauseResumeButtonClick(object? sender, RoutedEventArgs e)
    {
        var status = FrontendHostBridge.GetStatusSnapshot();
        if (status == null) return;
        if (status.RunStatus == RunStatus.Running)
        {
            FrontendHostBridge.SetRunState(0);
        }
        else if (status.RunStatus == RunStatus.Paused)
        {
            FrontendHostBridge.SetRunState(-1);
        }
    }

    private void OnStepButtonClick(object? sender, RoutedEventArgs e)
    {
        if (!MenuStepButton.IsEnabled)
            return;
        FrontendHostBridge.SetRunState(1);
    }
    
    private List<Key> DebuggerControlKeys = new() { Key.F5, Key.F8, Key.F9 };

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            switch (e.Key) // Handle special debugger buttons (F5, F8, F9)
            {
                // Assemble & run the editor program
                case Key.F5:
                    if (_debugUiActive)
                        _ = RunEditorProgramAsync();
                    e.Handled = true;
                    return;
                // Pause/Resume
                case Key.F9:
                    OnPauseResumeButtonClick(sender, e);
                    return;
                // Step
                case Key.F8:
                    OnStepButtonClick(sender, e);
                    return;
            }


            if (CodeEditorTextBox.TextArea.IsFocused)
                return;

            if (IsTextInputFocused())
                return;

            var keyboard = FrontendHostBridge.Keyboard;
            if (keyboard == null)
                return;

            //Log.Verbose("Key down: {Key}, Symbol: {Symbol}, Ctrl: {Ctrl}, Shift: {Shift}, Alt: {Alt}", e.Key, e.KeySymbol, ctrl, shift, alt);

            if (keyboard.KeyDown(e.Key.ToString(), e.KeySymbol))
                e.Handled = true;
        }
        catch (Exception x)
        {
            Log.Error(x.ToString());
        }
    }
    
    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (DebuggerControlKeys.Contains(e.Key))
        {
            // Do not send key up events for debugger control keys to avoid issues with key repeat and lost key up events
            return;
        }
        if (CodeEditorTextBox.TextArea.IsFocused)
            return;
        if (IsTextInputFocused())
            return;
        var keyboard = FrontendHostBridge.Keyboard;
        if (keyboard == null)
            return;

        if (keyboard.KeyUp(e.Key.ToString(), e.KeySymbol))
            e.Handled = true;
    }

    private bool IsTextInputFocused()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused is TextBox;
    }

    private void OnToggleDebuggerClick(object? sender, RoutedEventArgs e)
    {
        bool show;
        if (sender is MenuItem menuItem)
        {
            show = menuItem.IsChecked;
            ShowDebuggerMenuItem.IsChecked = show;
        }
        else
        {
            show = ShowDebuggerMenuItem.IsChecked;
        }
        SetDebugUiActive(show);
    }

    private void SetDebugUiActive(bool enabled)
    {
        _debugUiActive = enabled;
        ShowDebuggerMenuItem.IsChecked = enabled;

        var cols = WorkspaceGrid.ColumnDefinitions;

        IgnoreBreakpointsToggle.IsVisible = enabled;
        // Column 0: Left Sidebar, 1: Splitter, 3: Splitter, 4: Code Panel
        LeftSidebar.IsVisible = enabled;
        LeftSplitter.IsVisible = enabled;
        RightSplitter.IsVisible = enabled;
        CodePanel.IsVisible = enabled;

        // Lower panel (Breakpoints) and its splitter
        LowerPanel.IsVisible = enabled;
        LowerPanelSplitter.IsVisible = enabled;

        if (enabled)
        {
            cols[0].Width = new GridLength(300);
            cols[0].MinWidth = 220;
            cols[1].Width = new GridLength(4);
            cols[3].Width = new GridLength(4);
            cols[4].Width = new GridLength(400);
            cols[4].MinWidth = 220;

            // Restore screen row split
            var rows = MainColumn.RowDefinitions;
            rows[2].Height = new GridLength(4);
            rows[3].Height = new GridLength(250);
        }
        else
        {
            cols[0].Width = new GridLength(0);
            cols[0].MinWidth = 0;
            cols[1].Width = new GridLength(0);
            cols[3].Width = new GridLength(0);
            cols[4].Width = new GridLength(0);
            cols[4].MinWidth = 0;

            // Screen takes full height (no lower panel)
            var rows = MainColumn.RowDefinitions;
            rows[2].Height = new GridLength(0);
            rows[3].Height = new GridLength(0);
        }
    }

    private void OnIgnoreBreakpointsClick(object? sender, RoutedEventArgs e)
    {
        var debugger = FrontendHostBridge.Debugger;
        if (debugger == null) return;

        // Sync state from whichever control was clicked
        bool ignore;
        if (sender is MenuItem menuItem)
        {
            ignore = menuItem.IsChecked;
            IgnoreBreakpointsToggle.IsChecked = ignore;
        }
        else
        {
            ignore = IgnoreBreakpointsToggle.IsChecked == true;
            IgnoreBreakpointsMenuItem.IsChecked = ignore;
        }

        debugger.IgnoreBreakpoints = ignore;
    }

    private void AddBreakpointHeader()
    {
        var headerRow = CreateBreakpointRowGrid();
        headerRow.Children.Add(CreateBreakpointCell(new TextBlock
        {
            Text = string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0));

        headerRow.Children.Add(CreateBreakpointCell(new TextBlock
        {
            Text = "Line",
            VerticalAlignment = VerticalAlignment.Center,
        }, 1, "breakpoint-column-header"));

        headerRow.Children.Add(CreateBreakpointCell(new TextBlock
        {
            Text = "Code",
            VerticalAlignment = VerticalAlignment.Center,
        }, 2, "breakpoint-column-header"));

        BreakpointsPanel.Children.Add(new Border
        {
            Child = headerRow,
            Classes = { "breakpoint-header-row" }
        });
    }

    private void AddBreakpointPlaceholder(string text)
    {
        var placeholder = new TextBlock
        {
            Text = text,
            Foreground = Brush.Parse("#8191B0"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        placeholder.Classes.Add("code-text");
        BreakpointsPanel.Children.Add(new Border
        {
            Child = placeholder,
            Classes = { "breakpoint-row" }
        });
    }

    private string GetLineText(int line)
    {
        if (line - 1 >= _codeLines.Count)
            return "ERROR";
        return _codeLines[line-1];
    }

    private void AddBreakpointRow(int line)
    {
        var row = CreateBreakpointRowGrid();
        var enabled = _codeEditorBreakpointLines[line];

        var enabledToggle = new CheckBox
        {
            IsChecked = enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = line,
        };
        enabledToggle.IsCheckedChanged += OnBreakpointCheckedChanged;
        row.Children.Add(CreateBreakpointCell(enabledToggle, 0));

        var lineNumberText = new TextBlock
        {
            Text = $"{line}",
            Foreground = Brush.Parse(enabled ? "#00FF8E" : "#8191B0"),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = line,
            TextDecorations = TextDecorations.Underline,
        };
        lineNumberText.PointerPressed += OnBreakpointLineNumberClicked;
        row.Children.Add(CreateBreakpointCell(lineNumberText, 1, "code-text"));

        var codeText = GetLineText(line).TrimStart();
        var codeBlock = new TextBlock
        {
            Text = codeText,
            Foreground = Brush.Parse(enabled ? "#00FF8E" : "#8191B0"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(codeBlock, codeText);
        row.Children.Add(CreateBreakpointCell(codeBlock, 2, "code-text"));

        var deleteButton = new Button
        {
            Content = "×",
            Tag = line,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        deleteButton.Classes.Add("breakpoint-delete");
        deleteButton.Click += OnBreakpointDeleteClick;
        row.Children.Add(CreateBreakpointCell(deleteButton, 3));

        var status = _armedBreakpoints.TryGetValue(line, out var address)
            ? (enabled ? $"Active at ${address:X4}" : $"Disabled (${address:X4})")
            : "Not active yet: takes effect after the next Assemble or Run";
        ToolTip.SetTip(lineNumberText, status);

        BreakpointsPanel.Children.Add(new Border
        {
            Child = row,
            Classes = { "breakpoint-row" }
        });
    }

    private Grid CreateBreakpointRowGrid()
    {
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("40,60,*,36"),
            ColumnSpacing = 8,
        };
    }

    private Control CreateBreakpointCell(Control control, int column, params string[] classes)
    {
        Grid.SetColumn(control, column);
        foreach (var className in classes)
        {
            control.Classes.Add(className);
        }

        return control;
    }

    private void OnBreakpointLineNumberClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { Tag: int line })
            return;
        JumpToEditorLine(line);
        e.Handled = true;
    }

    private void JumpToEditorLine(int line)
    {
        CodeEditorTextBox.TextArea.Caret.Line = line;
        CodeEditorTextBox.TextArea.Caret.Column = 1;
        CodeEditorTextBox.TextArea.Caret.BringCaretToView();
        CodeEditorTextBox.Focus();
    }

    private void OnBreakpointCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: int line } checkBox)
            return;

        _codeEditorBreakpointLines[line] = checkBox.IsChecked ?? false;
        RefreshBreakpointsPanel();
    }

    private void OnBreakpointDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int line)
            return;

        _codeEditorBreakpointLines.Remove(line);
        RefreshBreakpointsPanel();
    }
    
    private async void OnLoadAssemblyClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Load Assembly Source",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Assembly files")
                    {
                        Patterns = ["*.asm"]
                    },
                    new FilePickerFileType("All files")
                    {
                        Patterns = ["*.*"]
                    }
                ]
            });

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (path == null)
            return;

        // save path of original (unmodified) asm file for later tass actions like include paths
        originalAssemblyFile = new FileInfo(path);
        LoadAssemblyIntoEditor(originalAssemblyFile);
    }
    
    private void LoadAssemblyIntoEditor(FileInfo fileInfo)
    {
        Log.Info("LoadAssemblyIntoEditor: {input}", Path.GetFullPath(fileInfo.FullName));
        if (!IsAssemblySourceFile(fileInfo))
        {
            MessageBox("Unsupported File", "Please choose a .asm file.");
            return;
        }

        if (!TryReadSource(fileInfo, out var codeLines, out var breakpointLines))
        {
            MessageBox("Load Error", $"Could not read source file:\n{fileInfo.FullName}");
            return;
        }

        _codeEditorBreakpointLines.Clear();
        _addressToSourceLine.Clear(); // Mapping belongs to the previous file
        _armedBreakpoints.Clear();    // Its breakpoints are gone; the sync below removes them from the debugger
        _codeLines = codeLines;
        var sourceText = string.Join(Environment.NewLine, _codeLines);

        AddMultipleBreakpoints(breakpointLines.ToArray());

        _loadedAssemblyPath = fileInfo.FullName;
        _isAssemblyDirty = false;

        _suppressCodeEditorEvents = true;
        CodeEditorTextBox.Text = sourceText;
        _suppressCodeEditorEvents = false;
        ClearErrorHighlights();
        SetErrorList([], NoErrorsText);

        SetCodePanelState(fileInfo.Name, sourceText);
        SetAssemblyButtonsEnabled(true);
        SendAssemblyOverUartButton.IsEnabled = false;
        WatchSourceFile(fileInfo);
    }

    /// <summary>
    /// Reads a source file and strips DBP lines, returning them as breakpoints on the following line.
    /// Retries briefly because an external editor may still be writing the file.
    /// </summary>
    private static bool TryReadSource(FileInfo fileInfo, out List<string> codeLines, out List<int> breakpointLines)
    {
        codeLines = [];
        breakpointLines = [];

        string[]? rawLines = null;
        for (var attempt = 1; rawLines is null; attempt++)
        {
            try
            {
                rawLines = File.ReadAllLines(fileInfo.FullName);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50); // File is probably still locked by the editor saving it
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read source file {File}", fileInfo.FullName);
                return false;
            }
        }

        foreach (var line in rawLines)
        {
            if (line.Contains("DBP", StringComparison.OrdinalIgnoreCase))
            {
                // The breakpoint belongs to the next non-DBP line, which will land at
                // codeLines.Count + 1 (1-based) in the stripped output.
                breakpointLines.Add(codeLines.Count + 1);
            }
            else
            {
                codeLines.Add(line);
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces the editor contents with the file on disk, keeping caret, scroll position and
    /// breakpoints (except those past the new end of file). Discards unsaved editor changes.
    /// </summary>
    /// <returns>True if the editor contents changed.</returns>
    private bool ReloadEditorFromDisk()
    {
        if (_loadedAssemblyPath is null)
            return false;

        var fileInfo = new FileInfo(_loadedAssemblyPath);
        if (!fileInfo.Exists || !TryReadSource(fileInfo, out var codeLines, out var breakpointLines))
            return false;

        var newText = string.Join(Environment.NewLine, codeLines);
        static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n");
        if (Normalize(newText) == Normalize(CodeEditorTextBox.Text))
            return false; // Nothing changed (e.g. a save without edits)

        var caretOffset = CodeEditorTextBox.CaretOffset;
        var verticalOffset = CodeEditorTextBox.VerticalOffset;

        _codeLines = codeLines;
        _suppressCodeEditorEvents = true;
        CodeEditorTextBox.Text = newText;
        _suppressCodeEditorEvents = false;
        _isAssemblyDirty = false;
        _addressToSourceLine.Clear(); // Mapping belongs to the old text
        ClearErrorHighlights();

        foreach (var line in _codeEditorBreakpointLines.Keys.Where(line => line > codeLines.Count).ToList())
            _codeEditorBreakpointLines.Remove(line);
        AddMultipleBreakpoints(breakpointLines.ToArray());

        CodeEditorTextBox.CaretOffset = Math.Min(caretOffset, CodeEditorTextBox.Document.TextLength);
        // Restore scrolling after the new text has been laid out
        Dispatcher.UIThread.Post(() => CodeEditorTextBox.ScrollToVerticalOffset(verticalOffset), DispatcherPriority.Background);

        SetCodePanelState(fileInfo.Name, newText);
        SendAssemblyOverUartButton.IsEnabled = false; // The previously assembled binary is stale now
        Log.Info("Reloaded {File} from disk", fileInfo.FullName);
        return true;
    }

    private FileSystemWatcher? _sourceWatcher;
    private CancellationTokenSource? _reloadDebounce;

    /// <summary>
    /// Watches the loaded source file so saves from an external editor show up in the IDE.
    /// </summary>
    private void WatchSourceFile(FileInfo fileInfo)
    {
        _sourceWatcher?.Dispose();
        _sourceWatcher = null;

        if (fileInfo.DirectoryName is not { } directory)
            return;

        var target = fileInfo.FullName;
        void OnFileEvent(string path)
        {
            if (string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                Dispatcher.UIThread.Post(ScheduleReloadFromDisk);
        }

        try
        {
            // Watch the whole folder: editors such as Sublime Text save "atomically" by writing a
            // temp file and renaming it over the original, which a filename filter can miss.
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
            watcher.Created += (_, e) => OnFileEvent(e.FullPath);
            watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
            watcher.EnableRaisingEvents = true;
            _sourceWatcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not watch {File} for changes: {Message}", target, ex.Message);
        }
    }

    /// <summary>
    /// Debounces file events (one save raises several) and reloads the editor. Asks first if the
    /// editor has changes that would be lost.
    /// </summary>
    private async void ScheduleReloadFromDisk()
    {
        _reloadDebounce?.Cancel();
        var debounce = _reloadDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, debounce.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_isAssembling)
        {
            ScheduleReloadFromDisk(); // Don't swap the text while it is being assembled
            return;
        }

        try
        {
            if (_isAssemblyDirty)
            {
                var answer = await MessageBoxManager.GetMessageBoxStandard(
                        "File changed on disk",
                        $"{GetLoadedAssemblyFileName()} was changed outside the IDE.\n\nReload it and discard your changes in the editor?",
                        ButtonEnum.YesNo)
                    .ShowWindowDialogAsync(this);
                if (answer != ButtonResult.Yes)
                    return;
            }

            ReloadEditorFromDisk();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reloading source file failed");
        }
    }
    
    private void SetCodePanelState(string? fileName, string? sourceText)
    {
        var hasFile = !string.IsNullOrWhiteSpace(fileName);
        CodeEditor.IsVisible = hasFile;
        //CodeEditorGutterScrollViewer.IsVisible = hasFile;

        CodeFileNameText.Text = hasFile
            ? BuildCodePanelFileLabel(fileName!, sourceText)
            : "No .asm file loaded";
    }
    
    private string BuildCodePanelFileLabel(string fileName, string? sourceText)
    {
        var dirtySuffix = _isAssemblyDirty ? " *" : string.Empty;
        var lineCount = CountLines(sourceText);
        return $"{fileName}{dirtySuffix}  •  {lineCount} lines";
    }
    
    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                count++;
        }

        return count;
    }

    private async void OnCompileAssemblyClick(object? sender, RoutedEventArgs e)
    {
        await AssembleEditorAsync();
    }

    private bool _isAssembling;

    /// <summary>
    /// Assembles the editor contents. Errors go to the Errors tab.
    /// </summary>
    /// <returns>The assembled binary, or null if assembling failed or is already running.</returns>
    private async Task<FileInfo?> AssembleEditorAsync()
    {
        if (_isAssembling || originalAssemblyFile is null)
            return null;

        // Always read directly from the editor so edits are never missed.
        var editorLines = (CodeEditorTextBox.Text ?? string.Empty)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        // Breakpoints are not compiled in (they are address breakpoints in the debugger, see
        // SyncBreakpointsToDebugger), and the preprocessor keeps lines 1:1, so pre.asm lines are editor lines.
        var preAsmLineToSourceLine = new Dictionary<int, int>();
        for (int line = 1; line <= editorLines.Count; line++)
            preAsmLineToSourceLine[line] = line;

        var inputFile = new FileInfo("editor.asm");
        var sourceText = string.Join("\n", editorLines);

        // Assemble off the UI thread; any failure (bad syntax, missing 64tass, timeout, I/O)
        // is reported to the user instead of escaping the event handler and killing the app.
        _isAssembling = true;
        SetAssemblyButtonsEnabled(false);
        SendAssemblyOverUartButton.IsEnabled = false;
        ClearErrorHighlights();
        SetErrorList([], "Assembling...");
        try
        {
            await File.WriteAllTextAsync(inputFile.FullName, sourceText);
            var (output, addressToPreLine) = await CodyAssembler.AssembleFileWithMapAsync(inputFile, originalAssemblyFile);

            // Combine: address -> pre.asm line -> original editor line
            _addressToSourceLine = new Dictionary<ushort, int>();
            foreach (var (address, preLine) in addressToPreLine)
            {
                if (preAsmLineToSourceLine.TryGetValue(preLine, out var sourceLine))
                    _addressToSourceLine[address] = sourceLine;
            }
            RefreshBreakpointsPanel(); // Arms the breakpoints against the new addresses

            SetErrorList([], "Assembled successfully. No errors.");
            SendAssemblyOverUartButton.IsEnabled = true;
            return output;
        }
        catch (AssemblerException ex)
        {
            Log.Error(ex, "Assembling {file} failed", inputFile.FullName);
            ShowAssemblerErrors(ex, inputFile, preAsmLineToSourceLine);
            return null;
        }
        catch (Exception ex)
        {
            // Not a source error (missing 64tass, timeout, I/O): list it without a location.
            Log.Error(ex, "Assembling {file} failed", inputFile.FullName);
            SetErrorList([new ErrorListEntry(AssemblerDiagnosticSeverity.Error, null, 0, ex.Message, null)], NoErrorsText);
            LowerPanelTabs.SelectedItem = ErrorsTab;
            return null;
        }
        finally
        {
            _isAssembling = false;
            SetAssemblyButtonsEnabled(true);
        }
    }

    private void SetAssemblyButtonsEnabled(bool enabled)
    {
        CompileAssemblyButton.IsEnabled = enabled;
        RunAssemblyButton.IsEnabled = enabled;
        RunAssemblyMenuItem.IsEnabled = enabled;
    }

    private void OnRunAssemblyClick(object? sender, RoutedEventArgs e)
    {
        _ = RunEditorProgramAsync();
    }

    /// <summary>How Run [F5] gets the assembled program into the emulator.</summary>
    private enum RunMode
    {
        /// <summary>Reset into CodyBASIC, type LOAD 1,1, send the binary over UART1 (like on hardware).</summary>
        Basic,
        /// <summary>Write the image straight into memory and start it, skipping BASIC (like --as-cartridge).</summary>
        Cartridge
    }

    private RunMode _runMode = RunMode.Basic;

    private void OnRunModeClick(object? sender, RoutedEventArgs e)
    {
        SetRunMode(sender == RunModeCartridgeItem ? RunMode.Cartridge : RunMode.Basic);
    }

    private void SetRunMode(RunMode mode)
    {
        _runMode = mode;
        RunModeBasicItem.IsChecked = mode == RunMode.Basic;
        RunModeCartridgeItem.IsChecked = mode == RunMode.Cartridge;

        var (label, header, tip) = mode switch
        {
            RunMode.Cartridge => ("▶ Cart [F5]", "_Run Assembly as Cartridge [F5]",
                "Assemble, load directly into memory and start, skipping BASIC [F5]"),
            _ => ("▶ BASIC [F5]", "_Run Assembly via BASIC [F5]",
                "Assemble, reset, type LOAD 1,1 and send over UART [F5]"),
        };
        RunAssemblyButton.Content = label;
        RunAssemblyMenuItem.Header = header;
        ToolTip.SetTip(RunAssemblyButton, tip);
    }

    /// <summary>
    /// Run [F5]: picks up the latest saved file, assembles it and, if that succeeds, starts it in
    /// the selected <see cref="RunMode"/>.
    /// </summary>
    private async Task RunEditorProgramAsync()
    {
        if (_isAssembling || originalAssemblyFile is null)
            return;

        try
        {
            // The file watcher reloads on save, but F5 right after saving can beat its debounce.
            if (!_isAssemblyDirty)
                ReloadEditorFromDisk();

            var binary = await AssembleEditorAsync();
            if (binary is null)
                return; // Errors are in the Errors tab

            var mode = _runMode;
            var started = mode == RunMode.Cartridge
                ? FrontendHostBridge.RunAsCartridge(binary)
                : FrontendHostBridge.RunViaBasicLoad(binary);
            if (!started)
            {
                Log.Warn("Run requested but no emulator is attached");
                return;
            }

            if (mode == RunMode.Basic)
                SendAssemblyOverUartButton.IsEnabled = false; // Already sent as part of the run
            Log.Info("Running {file} ({mode})", binary.FullName, mode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Run failed");
            SetErrorList([new ErrorListEntry(AssemblerDiagnosticSeverity.Error, null, 0, $"Run failed: {ex.Message}", null)], NoErrorsText);
            LowerPanelTabs.SelectedItem = ErrorsTab;
        }
    }

    private const string NoErrorsText = "No errors.";

    /// <summary>One row in the Errors tab. <see cref="Line"/> is an editor line, or null if the
    /// message has no location in the editor (included file, tool failure).</summary>
    private sealed record ErrorListEntry(
        AssemblerDiagnosticSeverity Severity, int? Line, int Column, string Message, string? File);

    /// <summary>
    /// Maps 64tass diagnostics from editor._pre.asm back to editor lines, highlights them,
    /// fills the Errors tab and moves the caret to the first one.
    /// Diagnostics in included files cannot be shown in the editor and are listed by file name.
    /// </summary>
    private void ShowAssemblerErrors(AssemblerException ex, FileInfo inputFile, Dictionary<int, int> preAsmLineToSourceLine)
    {
        var preFilePath = Path.GetFullPath(FileUtils.GetWithChangedExtension(inputFile, "_pre.asm").FullName);
        var entries = new List<ErrorListEntry>();

        foreach (var diag in ex.Diagnostics)
        {
            if (diag.Severity == AssemblerDiagnosticSeverity.Note)
                continue;

            var inEditor = string.Equals(SafeFullPath(diag.File), preFilePath, StringComparison.OrdinalIgnoreCase);

            if (inEditor && preAsmLineToSourceLine.TryGetValue(diag.Line, out var editorLine))
            {
                var severity = diag.Severity == AssemblerDiagnosticSeverity.Warning ? "warning" : "error";
                _errorLineRenderer?.Add(editorLine, $"{severity}: {diag.Message}");
                entries.Add(new ErrorListEntry(diag.Severity, editorLine, diag.Column, diag.Message, GetLoadedAssemblyFileName()));
            }
            else
            {
                entries.Add(new ErrorListEntry(diag.Severity, null, 0, diag.Message, $"{Path.GetFileName(diag.File)}:{diag.Line}"));
            }
        }

        // Nothing could be parsed: show the raw 64tass output as a single entry.
        if (entries.Count == 0)
            entries.Add(new ErrorListEntry(AssemblerDiagnosticSeverity.Error, null, 0, ex.Message, null));

        CodeEditorTextBox.TextArea.TextView.InvalidateVisual();
        SetErrorList(entries, NoErrorsText);
        LowerPanelTabs.SelectedItem = ErrorsTab;

        if (entries.FirstOrDefault(entry => entry.Line is not null) is { Line: { } line } first)
            JumpToEditorPosition(line, first.Column, focus: false);
    }

    /// <summary>Removes the red line highlights and tooltips from the editor (the Errors tab is kept).</summary>
    private void ClearErrorHighlights()
    {
        if (_errorLineRenderer is null || _errorLineRenderer.Messages.Count == 0)
            return;

        _errorLineRenderer.Messages.Clear();
        HideErrorToolTip();
        CodeEditorTextBox.TextArea.TextView.InvalidateVisual();
    }

    /// <summary>Rebuilds the Errors tab. <paramref name="emptyText"/> is shown when there are no entries.</summary>
    private void SetErrorList(IReadOnlyList<ErrorListEntry> entries, string emptyText)
    {
        ErrorsPanel.Children.Clear();

        var errorCount = entries.Count(entry => entry.Severity == AssemblerDiagnosticSeverity.Error);
        ErrorBadge.IsVisible = entries.Count > 0;
        ErrorBadge.Background = Brush.Parse(errorCount > 0 ? "#C62828" : "#8A6D00");
        ErrorBadgeText.Text = entries.Count.ToString();

        if (entries.Count == 0)
        {
            var placeholder = new TextBlock
            {
                Text = emptyText,
                Foreground = Brush.Parse("#8191B0"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            placeholder.Classes.Add("code-text");
            ErrorsPanel.Children.Add(new Border { Child = placeholder, Classes = { "breakpoint-row" } });
            return;
        }

        var header = CreateErrorRowGrid();
        header.Children.Add(CreateBreakpointCell(new TextBlock { Text = "Line" }, 1, "breakpoint-column-header"));
        header.Children.Add(CreateBreakpointCell(new TextBlock { Text = "Description" }, 2, "breakpoint-column-header"));
        header.Children.Add(CreateBreakpointCell(new TextBlock { Text = "File" }, 3, "breakpoint-column-header"));
        ErrorsPanel.Children.Add(new Border { Child = header, Classes = { "breakpoint-header-row" } });

        foreach (var entry in entries)
            ErrorsPanel.Children.Add(CreateErrorRow(entry));
    }

    private Border CreateErrorRow(ErrorListEntry entry)
    {
        var isWarning = entry.Severity == AssemblerDiagnosticSeverity.Warning;
        var row = CreateErrorRowGrid();

        // Drawn badge instead of a symbol glyph: Windows renders ✖/⚠ from the emoji font in its own colors.
        var icon = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Brush.Parse(isWarning ? "#E0A800" : "#E5484D"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = isWarning ? "!" : "x",
                Foreground = Brush.Parse(isWarning ? "#1A191D" : "#FFFFFF"),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };
        ToolTip.SetTip(icon, isWarning ? "Warning" : "Error");
        row.Children.Add(CreateBreakpointCell(icon, 0));

        row.Children.Add(CreateBreakpointCell(new TextBlock
        {
            Text = entry.Line?.ToString() ?? "",
            Foreground = Brush.Parse("#F2F2F3"),
            VerticalAlignment = VerticalAlignment.Center,
        }, 1, "code-text"));

        var message = new TextBlock
        {
            Text = entry.Message,
            Foreground = Brush.Parse("#F2F2F3"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        row.Children.Add(CreateBreakpointCell(message, 2, "code-text"));

        var file = new TextBlock
        {
            Text = entry.File ?? "",
            Foreground = Brush.Parse("#8191B0"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(file, entry.File);
        row.Children.Add(CreateBreakpointCell(file, 3, "code-text"));

        var border = new Border { Child = row, Classes = { "breakpoint-row", "error-row" } };
        if (entry.Line is { } line)
        {
            border.Tapped += (_, _) => JumpToEditorPosition(line, entry.Column, focus: true);
        }
        else
        {
            border.Cursor = Cursor.Default;
        }

        return border;
    }

    private static Grid CreateErrorRowGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("24,50,*,120"),
        ColumnSpacing = 8,
    };

    private void JumpToEditorPosition(int line, int column, bool focus)
    {
        if (line < 1 || line > CodeEditorTextBox.Document.LineCount)
            return;

        CodeEditorTextBox.ScrollToLine(line);
        CodeEditorTextBox.TextArea.Caret.Line = line;
        CodeEditorTextBox.TextArea.Caret.Column = Math.Max(1, column);
        CodeEditorTextBox.TextArea.Caret.BringCaretToView();
        if (focus)
            CodeEditorTextBox.Focus();
    }

    /// <summary>
    /// Shows the assembler message(s) for the hovered line, if that line has errors.
    /// </summary>
    private void OnCodeEditorPointerHover(object? sender, PointerEventArgs e)
    {
        if (_errorLineRenderer is null || _errorLineRenderer.Messages.Count == 0)
            return;

        var position = CodeEditorTextBox.GetPositionFromPoint(e.GetPosition(CodeEditorTextBox));
        if (position is not { } pos || !_errorLineRenderer.Messages.TryGetValue(pos.Line, out var messages))
        {
            HideErrorToolTip();
            return;
        }

        ToolTip.SetTip(CodeEditorTextBox, string.Join("\n", messages));
        ToolTip.SetIsOpen(CodeEditorTextBox, true);
        e.Handled = true;
    }

    private void HideErrorToolTip()
    {
        ToolTip.SetIsOpen(CodeEditorTextBox, false);
        // Clear the tip too, otherwise Avalonia's tooltip service would keep showing
        // the last message anywhere over the editor.
        ToolTip.SetTip(CodeEditorTextBox, null);
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private void OnSendAssemblyOverUartClick(object? sender, RoutedEventArgs e)
    {
        var file = new FileInfo("editor.bin");
        FrontendHostBridge.LoadUartSource(file);
        Log.Info("Sent {path} to Uart", file.FullName);
        SendAssemblyOverUartButton.IsEnabled = false;
    }

    private void OnCodeEditorDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_suppressCodeEditorEvents)
            return;

        _isAssemblyDirty = true;
        _addressToSourceLine.Clear(); // Clear mapping
        ClearErrorHighlights();       // Line numbers may have shifted; stale markers would mislead
        var sourceText = CodeEditorTextBox.Text ?? string.Empty;
        _codeLines = sourceText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        SetCodePanelState(GetLoadedAssemblyFileName(), sourceText);
    }
    
    private string GetLoadedAssemblyFileName()
    {
        if (string.IsNullOrWhiteSpace(_loadedAssemblyPath))
            return "Unnamed assembly";

        return Path.GetFileName(_loadedAssemblyPath);
    }

    private static bool IsAssemblySourceFile(FileInfo fileInfo)
    {
        var extension = fileInfo.Extension;
        return extension.Equals(".asm", StringComparison.OrdinalIgnoreCase);
    }


    private void AddMultipleBreakpoints(int[] lineNumbers)
    {
        foreach (int number in lineNumbers)
        {
            _codeEditorBreakpointLines.TryAdd(number, true);
        }
        RefreshBreakpointsPanel(); // Only refresh panel once
    }

    private void RemoveBreakpoint(int lineNumber)
    {
        try
        {
            _codeEditorBreakpointLines.Remove(lineNumber);
            RefreshBreakpointsPanel();
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private void ToggleBreakpoint(int lineNumber)
    {
        if (!_codeEditorBreakpointLines.TryAdd(lineNumber, true))
            _codeEditorBreakpointLines.Remove(lineNumber, out _);
        RefreshBreakpointsPanel();
    }

    private void OnCpuResetButtonClick(object? sender, RoutedEventArgs e)
    {
        FrontendHostBridge.ResetEmulator(); // TODO: This seems to delete the set frequency?
    }
}
