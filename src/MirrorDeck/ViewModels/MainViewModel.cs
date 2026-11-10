using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MirrorDeck.Core;
using MirrorDeck.Services;

namespace MirrorDeck.ViewModels;

/// <summary>
/// Everything the window shows and does. The receiver itself is <see cref="EngineHost"/>; this class turns
/// its status into text, the form into <see cref="AppSettings"/>, and buttons into actions.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 500;
    private const long MaxLogFileBytes = 2 * 1024 * 1024;

    private static readonly Brush Grey = Frozen(0x8A, 0x8A, 0x8A);
    private static readonly Brush Green = Frozen(0x10, 0x9B, 0x4A);
    private static readonly Brush Blue = Frozen(0x25, 0x63, 0xEB);
    private static readonly Brush Amber = Frozen(0xD9, 0x8E, 0x04);
    private static readonly Brush Red = Frozen(0xD1, 0x34, 0x38);

    private readonly Dispatcher _dispatcher;
    private readonly EngineHost _host;
    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly Lock _logGate = new();
    private readonly List<string> _pendingLog = [];
    private readonly Queue<string> _logLines = new();

    private ReceiverStatus _status = ReceiverStatus.Stopped;
    private string _runningName = string.Empty;   // the name the engine was started with
    private bool _hasUnappliedChanges;
    private EngineLocation? _engine;
    private IReadOnlyList<string> _searched = [];
    private string? _appliedFingerprint;
    private DateTime _lastDiagnosticsUtc = DateTime.MinValue;
    private bool _diagnosticsRunning;
    private bool _loading;
    private bool _shuttingDown;
    private long _logFileBytes;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _host = new EngineHost(new EngineHostOptions
        {
            DataDirectory = AppPaths.EngineDataDirectory,
            RequestGracefulStop = EngineProcessSupport.RequestStop,
            OnProcessStarted = EngineProcessSupport.TieLifetimeToApp,
        });
        _host.StatusChanged += status => _dispatcher.BeginInvoke(() => ApplyStatus(status));
        _host.LogLine += line =>
        {
            lock (_logGate) _pendingLog.Add(line);
        };

        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => FlushLog(), dispatcher);
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveIfValid();
        };

        StartStopCommand = new RelayCommand(StartOrStopAsync, () => !_shuttingDown && _status.State != ReceiverState.Stopping, ShowError);
        ApplyCommand = new RelayCommand(StartAsync, () => HasUnappliedChanges && !_shuttingDown, ShowError);
        ShowMirrorWindowCommand = new RelayCommand(ShowMirrorWindow, () => IsShowingPicture, ShowError);
        ToggleFullscreenCommand = new RelayCommand(ToggleFullscreenAsync, () => IsShowingPicture && VideoSinkIndex != 2, ShowError);
        RefreshDiagnosticsCommand = new RelayCommand(() => RefreshDiagnosticsAsync(force: true), () => !_diagnosticsRunning, ShowError);
        ForgetDevicesCommand = new RelayCommand(ForgetDevicesAsync, onError: ShowError);
        BrowseEngineCommand = new RelayCommand(BrowseEngine, onError: ShowError);
        CopyLogCommand = new RelayCommand(() => Clipboard.SetText(LogText.Length > 0 ? LogText : "(empty)"), onError: ShowError);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder, onError: ShowError);
        OpenLicensesCommand = new RelayCommand(OpenLicenses, onError: ShowError);

        LoadForm(SettingsStore.Load());
        ApplyStatus(ReceiverStatus.Stopped);
        StartLogFile();
    }

    // ---------------------------------------------------------------- commands

    public ICommand StartStopCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand ShowMirrorWindowCommand { get; }

    public ICommand ToggleFullscreenCommand { get; }

    public ICommand RefreshDiagnosticsCommand { get; }

    public ICommand ForgetDevicesCommand { get; }

    public ICommand BrowseEngineCommand { get; }

    public ICommand CopyLogCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenLicensesCommand { get; }

    // ---------------------------------------------------------------- status (read-only for the view)

    private string _statusTitle = string.Empty;
    private string _statusDetail = string.Empty;
    private Brush _statusBrush = Grey;
    private string _startStopLabel = "Start";
    private string? _pin;
    private string? _notice;
    private bool _isBusy;
    private bool _isShowingPicture;
    private string? _formError;
    private string _logText = string.Empty;

    public string StatusTitle
    {
        get => _statusTitle;
        private set => Set(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => Set(ref _statusDetail, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => Set(ref _statusBrush, value);
    }

    public string StartStopLabel
    {
        get => _startStopLabel;
        private set => Set(ref _startStopLabel, value);
    }

    /// <summary>The code to type on the Apple device, while one is being asked for.</summary>
    public string? Pin
    {
        get => _pin;
        private set
        {
            if (Set(ref _pin, value)) Raise(nameof(ShowPin));
        }
    }

    public bool ShowPin => !string.IsNullOrEmpty(_pin);

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value)) Raise(nameof(ShowNotice));
        }
    }

    public bool ShowNotice => !string.IsNullOrEmpty(_notice);

    /// <summary>Starting or stopping: shows the progress bar.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>A mirror or video window exists right now.</summary>
    public bool IsShowingPicture
    {
        get => _isShowingPicture;
        private set => Set(ref _isShowingPicture, value);
    }

    /// <summary>Why Start/Apply did not do anything (invalid name, password too short, ...).</summary>
    public string? FormError
    {
        get => _formError;
        private set
        {
            if (Set(ref _formError, value)) Raise(nameof(ShowFormError));
        }
    }

    public bool ShowFormError => !string.IsNullOrEmpty(_formError);

    /// <summary>The engine is running with settings that differ from what the form shows.</summary>
    public bool HasUnappliedChanges
    {
        get => _hasUnappliedChanges;
        private set => Set(ref _hasUnappliedChanges, value);
    }

    private void UpdateUnappliedChanges() =>
        HasUnappliedChanges = _status.IsRunning
                              && _appliedFingerprint is not null
                              && Fingerprint(BuildSettings()) != _appliedFingerprint;

    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; } = [];

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    public string VersionText { get; } =
        $"MirrorDeck {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev"}";

    // ---------------------------------------------------------------- the form (two-way bound)

    private string _receiverName = string.Empty;
    private string? _nameError;
    private AccessMode _accessMode;
    private string _password = string.Empty;
    private string? _passwordError;
    private bool _startFullscreen;
    private bool _audioEnabled = true;
    private bool _keepDisplayAwake = true;
    private bool _autoStart = true;
    private bool _newConnectionTakesOver = true;
    private bool _enableH265;
    private bool _enableHls;
    private int _videoSinkIndex;
    private int _fpsIndex = 1;
    private string _enginePathOverride = string.Empty;
    private string _extraEngineOptions = string.Empty;

    public string ReceiverName
    {
        get => _receiverName;
        set
        {
            if (!Set(ref _receiverName, value ?? string.Empty)) return;
            NameError = AppSettings.ValidateName(_receiverName);
            FormChanged();
        }
    }

    public string? NameError
    {
        get => _nameError;
        private set
        {
            if (Set(ref _nameError, value)) Raise(nameof(ShowNameError));
        }
    }

    public bool ShowNameError => !string.IsNullOrEmpty(_nameError);

    public bool IsAccessOpen
    {
        get => _accessMode == AccessMode.Open;
        set => SetAccessMode(AccessMode.Open, value);
    }

    public bool IsAccessPairingPin
    {
        get => _accessMode == AccessMode.PairingPin;
        set => SetAccessMode(AccessMode.PairingPin, value);
    }

    public bool IsAccessPinEveryTime
    {
        get => _accessMode == AccessMode.PinEveryTime;
        set => SetAccessMode(AccessMode.PinEveryTime, value);
    }

    public bool IsAccessPassword
    {
        get => _accessMode == AccessMode.Password;
        set => SetAccessMode(AccessMode.Password, value);
    }

    /// <summary>Not bindable from a PasswordBox; the window pushes changes in here.</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (!Set(ref _password, value ?? string.Empty)) return;
            UpdatePasswordError();
            FormChanged();
        }
    }

    public string? PasswordError
    {
        get => _passwordError;
        private set
        {
            if (Set(ref _passwordError, value)) Raise(nameof(ShowPasswordError));
        }
    }

    public bool ShowPasswordError => !string.IsNullOrEmpty(_passwordError);

    public bool StartFullscreen
    {
        get => _startFullscreen;
        set
        {
            if (Set(ref _startFullscreen, value)) FormChanged();
        }
    }

    public bool AudioEnabled
    {
        get => _audioEnabled;
        set
        {
            if (Set(ref _audioEnabled, value)) FormChanged();
        }
    }

    public bool KeepDisplayAwake
    {
        get => _keepDisplayAwake;
        set
        {
            if (!Set(ref _keepDisplayAwake, value)) return;
            UpdateDisplayAwake();
            FormChanged();
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (Set(ref _autoStart, value)) FormChanged();
        }
    }

    public bool NewConnectionTakesOver
    {
        get => _newConnectionTakesOver;
        set
        {
            if (Set(ref _newConnectionTakesOver, value)) FormChanged();
        }
    }

    public bool EnableH265
    {
        get => _enableH265;
        set
        {
            if (Set(ref _enableH265, value)) FormChanged();
        }
    }

    public bool EnableHls
    {
        get => _enableHls;
        set
        {
            if (Set(ref _enableHls, value)) FormChanged();
        }
    }

    /// <summary>0 = Direct3D 11, 1 = Direct3D 12, 2 = automatic.</summary>
    public int VideoSinkIndex
    {
        get => _videoSinkIndex;
        set
        {
            if (Set(ref _videoSinkIndex, Math.Clamp(value, 0, 2))) FormChanged();
        }
    }

    /// <summary>0 = 30 frames per second, 1 = 60.</summary>
    public int FpsIndex
    {
        get => _fpsIndex;
        set
        {
            if (Set(ref _fpsIndex, Math.Clamp(value, 0, 1))) FormChanged();
        }
    }

    public string EnginePathOverride
    {
        get => _enginePathOverride;
        set
        {
            if (Set(ref _enginePathOverride, value ?? string.Empty)) FormChanged();
        }
    }

    public string ExtraEngineOptions
    {
        get => _extraEngineOptions;
        set
        {
            if (Set(ref _extraEngineOptions, value ?? string.Empty)) FormChanged();
        }
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Called once, after the window is on screen.</summary>
    public async Task InitializeAsync()
    {
        LocateEngine();

        // Also when the engine is missing: the status card then says so right away, instead of
        // showing a receiver that is merely "off".
        var willStart = _autoStart;

        // Run the checks alongside the start. They are told that the receiver is (about to be) running,
        // otherwise our own engine grabbing its ports could be reported as "another program".
        var checks = RefreshDiagnosticsAsync(force: true, assumeRunning: willStart);
        if (willStart) await StartAsync();
        await checks;
    }

    /// <summary>Called when the window is activated: the user may just have answered a firewall prompt
    /// or changed something in Windows Settings.</summary>
    public Task OnWindowActivatedAsync() => RefreshDiagnosticsAsync(force: false);

    /// <summary>Stops the receiver (politely, so it disappears from AirPlay lists) and saves the form.</summary>
    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _saveTimer.Stop();
        _logTimer.Stop();
        SaveIfValid();
        try
        {
            await Task.Run(() => _host.StopAsync());
        }
        finally
        {
            FlushLog(); // the engine's last words belong in the log file too
            DisplayAwake.Set(false);
            _host.Dispose();
        }
    }

    // ---------------------------------------------------------------- actions

    private Task StartOrStopAsync() =>
        _status.State is ReceiverState.Stopped or ReceiverState.Failed ? StartAsync() : StopAsync();

    private async Task StartAsync()
    {
        var settings = BuildSettings();
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            FormError = string.Join(" ", problems);
            return;
        }

        FormError = null;
        SettingsStore.Save(settings);
        LocateEngine();
        _appliedFingerprint = Fingerprint(settings);
        _runningName = settings.ReceiverName;
        HasUnappliedChanges = false;

        // process creation and file I/O: keep it off the UI thread
        var engine = _engine;
        var searched = _searched;
        await Task.Run(() => _host.StartAsync(settings, engine, searched));
    }

    private async Task StopAsync()
    {
        await Task.Run(() => _host.StopAsync());
        _appliedFingerprint = null;
        HasUnappliedChanges = false;
    }

    private void ShowMirrorWindow()
    {
        if (_host.ProcessId is not { } pid) return;
        MirrorWindow.BringToFront(MirrorWindow.Find(pid));
    }

    private async Task ToggleFullscreenAsync()
    {
        if (_host.ProcessId is not { } pid) return;
        var window = MirrorWindow.Find(pid);
        if (window == IntPtr.Zero || !await MirrorWindow.ToggleFullscreenAsync(window))
        {
            Notice = "Could not reach the mirror window. Click it and press Alt+Enter to switch full screen.";
        }
    }

    private async Task ForgetDevicesAsync()
    {
        var answer = MessageBox.Show(
            "Forget all paired devices?\n\nEvery device will have to enter a new code the next time it connects. " +
            "On a device that was paired before, you may also have to pick this PC again in the AirPlay list.",
            "MirrorDeck",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var wasRunning = _status.IsRunning;
        if (wasRunning) await StopAsync();
        var register = Path.Combine(AppPaths.EngineDataDirectory, EngineConfig.PairingRegisterFileName);
        if (File.Exists(register)) File.Delete(register);
        if (wasRunning) await StartAsync();
    }

    private void BrowseEngine()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate uxplay.exe",
            Filter = "UxPlay engine (uxplay.exe)|uxplay.exe|Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        EnginePathOverride = dialog.FileName;
        LocateEngine();
        _ = RefreshDiagnosticsAsync(force: true);
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
    }

    private static void OpenLicenses()
    {
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        var target = File.Exists(notices) ? notices : Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        if (!File.Exists(target))
        {
            MessageBox.Show("The license files are missing from this installation.", "MirrorDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // .md has no default program on a clean Windows installation; Notepad always works
        Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { target }, UseShellExecute = false });
    }

    // ---------------------------------------------------------------- status handling

    private void ApplyStatus(ReceiverStatus status)
    {
        var previous = _status;
        _status = status;

        var who = string.IsNullOrWhiteSpace(status.ClientName) ? "A device" : status.ClientName!;
        var model = string.IsNullOrWhiteSpace(status.ClientModel) ? string.Empty : $" ({status.ClientModel})";
        var fullscreenHint = _videoSinkIndex == 2 ? string.Empty : " Alt+Enter in the mirror window switches full screen.";

        (StatusTitle, StatusDetail, StatusBrush) = status.State switch
        {
            ReceiverState.Stopped => ("Receiver is off", "Start it to make this PC appear in the AirPlay list of your Apple devices.", Grey),

            ReceiverState.Starting => ("Starting…", "Preparing the receiver. The very first start takes longer while video components are indexed.", Amber),

            ReceiverState.Ready => (
                "Ready",
                $"Visible as “{_runningName}”. On an iPhone or iPad open Control Centre and tap Screen Mirroring; on a Mac use the Screen Mirroring menu.",
                Green),

            ReceiverState.Connecting => ("Connecting…", $"{who}{model} is connecting.", Blue),

            ReceiverState.PinRequired => ("Enter this code on your device", "The device is showing a code prompt for this PC.", Amber),

            ReceiverState.Mirroring => ($"Mirroring {who}", $"Showing the screen of {who}{model}.{fullscreenHint}", Blue),

            ReceiverState.AudioStreaming => ($"Playing audio from {who}", $"{who}{model} is streaming sound to this PC.", Blue),

            ReceiverState.VideoStreaming => ("Playing video", $"A video is being cast to this PC.{fullscreenHint}", Blue),

            ReceiverState.Stopping => ("Stopping…", "Taking this PC off the AirPlay list.", Amber),

            _ => (FaultTitle(status.Fault), FaultText(status), Red),
        };

        Pin = status.State == ReceiverState.PinRequired ? status.Pin : null;
        Notice = status.State == ReceiverState.Failed
            ? FaultAdvice(status.Fault)
            : !status.LiveStatusAvailable && status.IsRunning
                ? "Mirroring works, but live connection status is not available because the engine's output is delayed (the debug option “d” causes this)."
                : status.Notice;

        IsBusy = status.State is ReceiverState.Starting or ReceiverState.Stopping;
        IsShowingPicture = status.State is ReceiverState.Mirroring or ReceiverState.VideoStreaming;
        StartStopLabel = status.State is ReceiverState.Stopped or ReceiverState.Failed ? "Start" : "Stop";
        UpdateUnappliedChanges();
        UpdateDisplayAwake();

        if (IsShowingPicture && previous.State != status.State) _ = TitleMirrorWindowAsync(who);

        // The firewall and port picture changes exactly at these moments.
        var settled = status.State is ReceiverState.Ready or ReceiverState.Failed or ReceiverState.Stopped;
        if (settled && previous.State != status.State) _ = RefreshDiagnosticsAsync(force: true);

        CommandManager.InvalidateRequerySuggested();
    }

    private static string FaultTitle(EngineFault fault) => fault switch
    {
        EngineFault.EngineNotFound => "AirPlay engine not found",
        EngineFault.InvalidSettings => "Settings need attention",
        EngineFault.PortInUse => "The AirPlay ports are in use",
        EngineFault.DiscoveryFailed => "This PC cannot be announced on the network",
        EngineFault.MissingPlugin => "The engine installation is incomplete",
        EngineFault.RendererFailed => "Video or audio output failed",
        EngineFault.StartTimeout => "The receiver did not start",
        _ => "The receiver stopped unexpectedly",
    };

    private static string FaultText(ReceiverStatus status)
    {
        var detail = status.FaultDetail ?? "The receiver stopped.";

        // The list of folders that were searched is in the set-up check and in the log; here it only
        // pushes the rest of the window down.
        const string searched = " Looked in:";
        var cut = status.Fault == EngineFault.EngineNotFound ? detail.IndexOf(searched, StringComparison.Ordinal) : -1;
        return cut > 0 ? detail[..cut] : detail;
    }

    private static string? FaultAdvice(EngineFault fault) => fault switch
    {
        EngineFault.PortInUse => "Only one AirPlay receiver can run on a PC at a time. Close the other one, then press Start.",
        EngineFault.DiscoveryFailed => "Check that the PC is connected to a network. This build of the engine has its own discovery service and does not need Apple's Bonjour; an engine built with USE_DNS_SD does, and then the “Bonjour Service” has to be installed and running.",
        EngineFault.MissingPlugin => "Rebuild the engine bundle with engine/build-engine.sh, or reinstall the app.",
        EngineFault.RendererFailed => "Choose a different renderer under Advanced (Direct3D 11 works on every Windows 10/11 PC) and press Start.",
        EngineFault.StartTimeout => "Security software scanning the engine can slow the first start down a lot. Press Start to try again; the log below shows how far it got.",
        EngineFault.Crashed => "The log below has the details. If it happens with a particular device or app, please include the log in a bug report.",
        _ => null,
    };

    /// <summary>The engine's window is called "Direct3D11 renderer"; give it a name people recognise.</summary>
    private async Task TitleMirrorWindowAsync(string who)
    {
        // the window is created when the first frame has been decoded
        for (var attempt = 0; attempt < 40 && IsShowingPicture; attempt++)
        {
            if (_host.ProcessId is not { } pid) return;
            var window = MirrorWindow.Find(pid);
            if (window != IntPtr.Zero)
            {
                var hint = _videoSinkIndex == 2 ? string.Empty : "  (Alt+Enter: full screen)";
                MirrorWindow.SetTitle(window, $"{who} – {_runningName}{hint}");
                return;
            }

            await Task.Delay(250);
        }
    }

    private void UpdateDisplayAwake() => DisplayAwake.Set(_keepDisplayAwake && IsShowingPicture);

    // ---------------------------------------------------------------- diagnostics

    private async Task RefreshDiagnosticsAsync(bool force, bool assumeRunning = false)
    {
        if (_diagnosticsRunning || _shuttingDown) return;
        if (!force && DateTime.UtcNow - _lastDiagnosticsUtc < TimeSpan.FromSeconds(5)) return;

        _diagnosticsRunning = true;
        try
        {
            var engine = _engine;
            var searched = _searched;
            var running = assumeRunning || _status.IsRunning;
            var results = await Task.Run(() => Services.Diagnostics.Run(engine, searched, running));

            Diagnostics.Clear();
            foreach (var result in results) Diagnostics.Add(ToViewModel(result));
            _lastDiagnosticsUtc = DateTime.UtcNow;
        }
        finally
        {
            _diagnosticsRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private DiagnosticItemViewModel ToViewModel(DiagnosticResult result)
    {
        var (glyph, brush) = result.Severity switch
        {
            Severity.Ok => ("✓", Green),
            Severity.Info => ("i", Blue),
            Severity.Warning => ("!", Amber),
            _ => ("✕", Red),
        };

        var (label, command) = result.Fix switch
        {
            FixKind.AllowThroughFirewall => ("Allow through Windows Firewall…", new RelayCommand(AllowThroughFirewallAsync, onError: ShowError)),
            FixKind.OpenNetworkSettings => ("Open network settings", new RelayCommand(OpenNetworkSettings, onError: ShowError)),
            FixKind.ChooseEngine => ("Locate uxplay.exe…", new RelayCommand(BrowseEngine, onError: ShowError)),
            _ => ((string?)null, (RelayCommand?)null),
        };

        return new DiagnosticItemViewModel
        {
            Glyph = glyph,
            Brush = brush,
            Title = result.Title,
            Detail = result.Detail,
            FixLabel = label,
            FixCommand = command,
        };
    }

    private async Task AllowThroughFirewallAsync()
    {
        if (_engine is null) return;
        var answer = MessageBox.Show(
            "MirrorDeck will add one Windows Firewall rule that lets devices on Private and Domain (home or work) " +
            "networks connect to the AirPlay engine:\n\n" + _engine.ExePath + "\n\n" +
            "Existing inbound rules for that program, including blocking ones, are replaced. Public networks stay blocked.\n\n" +
            "Windows will ask for administrator permission.",
            "MirrorDeck",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;

        var error = await FirewallService.AllowAsync(_engine.ExePath);
        if (error is not null) MessageBox.Show(error, "MirrorDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        await RefreshDiagnosticsAsync(force: true);
    }

    private static void OpenNetworkSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });

    // ---------------------------------------------------------------- form <-> settings

    private void LoadForm(AppSettings settings)
    {
        _loading = true;
        try
        {
            ReceiverName = settings.ReceiverName;
            _accessMode = settings.AccessMode;
            Password = settings.Password ?? string.Empty;
            StartFullscreen = settings.StartFullscreen;
            AudioEnabled = settings.AudioEnabled;
            KeepDisplayAwake = settings.KeepDisplayAwake;
            AutoStart = settings.AutoStart;
            NewConnectionTakesOver = settings.NewConnectionTakesOver;
            EnableH265 = settings.EnableH265;
            EnableHls = settings.EnableHls;
            VideoSinkIndex = settings.VideoSink switch
            {
                VideoSink.Direct3D12 => 1,
                VideoSink.Auto => 2,
                _ => 0,
            };
            FpsIndex = settings.MaxFps >= 45 ? 1 : 0;
            EnginePathOverride = settings.EnginePathOverride ?? string.Empty;
            ExtraEngineOptions = settings.ExtraEngineOptions ?? string.Empty;
            NameError = AppSettings.ValidateName(_receiverName);
            UpdatePasswordError();
        }
        finally
        {
            _loading = false;
        }
    }

    private AppSettings BuildSettings() => new()
    {
        ReceiverName = _receiverName.Trim(),
        AccessMode = _accessMode,
        Password = string.IsNullOrEmpty(_password) ? null : _password,
        StartFullscreen = _startFullscreen,
        AudioEnabled = _audioEnabled,
        KeepDisplayAwake = _keepDisplayAwake,
        AutoStart = _autoStart,
        NewConnectionTakesOver = _newConnectionTakesOver,
        EnableH265 = _enableH265,
        EnableHls = _enableHls,
        VideoSink = _videoSinkIndex switch
        {
            1 => VideoSink.Direct3D12,
            2 => VideoSink.Auto,
            _ => VideoSink.Direct3D11,
        },
        MaxFps = _fpsIndex == 1 ? 60 : 30,
        EnginePathOverride = string.IsNullOrWhiteSpace(_enginePathOverride) ? null : _enginePathOverride.Trim(),
        ExtraEngineOptions = string.IsNullOrWhiteSpace(_extraEngineOptions) ? null : _extraEngineOptions,
    };

    /// <summary>
    /// What the engine was started with. Two settings objects with the same fingerprint need no restart
    /// (changing "start with the app", for example, does not touch the engine).
    /// </summary>
    private static string Fingerprint(AppSettings settings) =>
        settings.Validate().Count > 0
            ? "invalid"
            : EngineConfig.BuildRcFile(settings) + "\n" + settings.EnginePathOverride;

    private void SetAccessMode(AccessMode mode, bool isChecked)
    {
        // a radio button also writes "false" to the one that loses the selection; ignore that half
        if (!isChecked || _accessMode == mode) return;
        _accessMode = mode;
        Raise(nameof(IsAccessOpen));
        Raise(nameof(IsAccessPairingPin));
        Raise(nameof(IsAccessPinEveryTime));
        Raise(nameof(IsAccessPassword));
        UpdatePasswordError();
        FormChanged();
    }

    private void UpdatePasswordError() =>
        PasswordError = _accessMode == AccessMode.Password ? AppSettings.ValidatePassword(_password) : null;

    private void FormChanged()
    {
        if (_loading) return;
        FormError = null;
        UpdateUnappliedChanges();
        CommandManager.InvalidateRequerySuggested();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveIfValid()
    {
        var settings = BuildSettings();
        if (settings.Validate().Count > 0) return;
        try
        {
            SettingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not worth interrupting the user for; the next change tries again
        }
    }

    private void LocateEngine()
    {
        var overridePath = string.IsNullOrWhiteSpace(_enginePathOverride) ? null : _enginePathOverride;
        _engine = EngineLocator.Find(overridePath, AppContext.BaseDirectory, out _searched);
    }

    // ---------------------------------------------------------------- log

    private void FlushLog()
    {
        List<string> lines;
        lock (_logGate)
        {
            if (_pendingLog.Count == 0) return;
            lines = [.. _pendingLog];
            _pendingLog.Clear();
        }

        foreach (var line in lines) _logLines.Enqueue(line);
        AppendToLogFile(lines);

        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        LogText = string.Join(Environment.NewLine, _logLines);
    }

    /// <summary>
    /// The same lines also go to a file next to the settings ("Open data folder"), so that a bug report
    /// can include them after the window has been closed. One file per run of the app, capped in size.
    /// </summary>
    private void StartLogFile()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.EngineLog, $"{VersionText}, started {DateTime.Now:u}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logFileBytes = MaxLogFileBytes; // not writable: do not try again
        }
    }

    private void AppendToLogFile(List<string> lines)
    {
        if (_logFileBytes >= MaxLogFileBytes) return;
        try
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            var text = string.Concat(lines.Select(l => $"{stamp} {l}{Environment.NewLine}"));
            File.AppendAllText(AppPaths.EngineLog, text);
            _logFileBytes += text.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logFileBytes = MaxLogFileBytes;
        }
    }

    private void ShowError(Exception exception)
    {
        if (_shuttingDown) return;
        MessageBox.Show(exception.Message, "MirrorDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
