using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RhythmLume.Core;
using RhythmLume.Hue;

namespace RhythmLume.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting;

    public async void Execute(object? parameter)
    {
        if (_isExecuting)
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class LightItemViewModel : ObservableObject
{
    private bool _isSelected;
    private LightPosition _position;
    private int _order;

    public LightItemViewModel(HueLight light)
    {
        Light = light;
        _position = light.Position;
        _order = light.Order;
    }

    public event Action? SelectionChanged;

    public HueLight Light { get; }
    public string Id => Light.Id;
    public string Name => Light.Name;
    public string ModelId => Light.ModelId;
    public bool IsReachable => Light.IsReachable;
    public bool SupportsColor => Light.SupportsColor;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    public LightPosition Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    public int Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, Math.Max(0, value)))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    public HueLight ToDomain() => Light with { Position = Position, Order = Order };
}

public sealed class ColorSwatchViewModel : ObservableObject
{
    private string _hex;
    private Brush _brush;

    public ColorSwatchViewModel(RgbColor color)
    {
        _hex = ToHex(color);
        _brush = CreateBrush(color);
    }

    public event Action? ColorChanged;

    public string Hex
    {
        get => _hex;
        set
        {
            if (!TryParse(value, out var color) || !SetProperty(ref _hex, value.ToUpperInvariant()))
            {
                return;
            }

            Brush = CreateBrush(color);
            ColorChanged?.Invoke();
        }
    }

    public Brush Brush
    {
        get => _brush;
        private set => SetProperty(ref _brush, value);
    }

    public RgbColor ToColor() => TryParse(Hex, out var color) ? color : RgbColor.White;

    private static bool TryParse(string? value, out RgbColor color)
    {
        color = RgbColor.White;
        var hex = value?.Trim().TrimStart('#');
        if (hex?.Length != 6 ||
            !byte.TryParse(hex[..2], NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, null, out var blue))
        {
            return false;
        }

        color = new(red / 255f, green / 255f, blue / 255f);
        return true;
    }

    private static string ToHex(RgbColor color) =>
        $"#{(byte)(Math.Clamp(color.Red, 0, 1) * 255):X2}" +
        $"{(byte)(Math.Clamp(color.Green, 0, 1) * 255):X2}" +
        $"{(byte)(Math.Clamp(color.Blue, 0, 1) * 255):X2}";

    private static Brush CreateBrush(RgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)(Math.Clamp(color.Red, 0, 1) * 255),
            (byte)(Math.Clamp(color.Green, 0, 1) * 255),
            (byte)(Math.Clamp(color.Blue, 0, 1) * 255)));
        brush.Freeze();
        return brush;
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IAudioDeviceService _audioDevices;
    private readonly IHueBridgeDiscovery _bridgeDiscovery;
    private readonly IHueAuthenticator _authenticator;
    private readonly IHueCapabilityDetector _capabilityDetector;
    private readonly IHueBridgeClient _bridgeClient;
    private readonly IHueStreamingClient _streamingClient;
    private readonly ICredentialStore _credentialStore;
    private readonly ISettingsStore _settingsStore;
    private readonly AnalysisOptions _analysisOptions;
    private readonly MusicOrchestrator _orchestrator;
    private readonly IDiagnosticsSink _diagnostics;
    private AppSettings _settings = new();
    private HueCredentials? _credentials;
    private HueBridgeCapabilities? _capabilities;
    private AudioDeviceInfo? _selectedAudioDevice;
    private HueBridgeInfo? _selectedBridge;
    private EntertainmentConfiguration? _selectedEntertainmentConfiguration;
    private PresetDefinition? _selectedPreset;
    private LightItemViewModel? _selectedLight;
    private ColorSwatchViewModel? _selectedSwatch;
    private string _manualBridgeAddress = string.Empty;
    private string _statusMessage = "Ready to set up";
    private string _errorMessage = string.Empty;
    private string _audioStatus = "Stopped";
    private string _bridgeStatus = "Not connected";
    private string _bridgeMode = "Disconnected";
    private string _currentInput = "No audio input";
    private string _lastBeat = "Waiting for audio";
    private bool _isBusy;
    private bool _isShowRunning;
    private bool _useEntertainment = true;
    private double _inputLevel;
    private double _bassActivity;
    private double _subBassLevel;
    private double _lowMidsLevel;
    private double _midsLevel;
    private double _highsLevel;
    private float[] _spectrum = new float[96];
    private int _hueUpdateRate;
    private long _droppedAudioChunks;
    private long _failedHueFrames;
    private AnalysisFrame? _latestFrame;
    private int _uiUpdateQueued;

    public MainViewModel(
        IAudioDeviceService audioDevices,
        IHueBridgeDiscovery bridgeDiscovery,
        IHueAuthenticator authenticator,
        IHueCapabilityDetector capabilityDetector,
        IHueBridgeClient bridgeClient,
        IHueStreamingClient streamingClient,
        ICredentialStore credentialStore,
        ISettingsStore settingsStore,
        AnalysisOptions analysisOptions,
        MusicOrchestrator orchestrator,
        IPresetStore presetStore,
        IDiagnosticsSink diagnostics)
    {
        _audioDevices = audioDevices;
        _bridgeDiscovery = bridgeDiscovery;
        _authenticator = authenticator;
        _capabilityDetector = capabilityDetector;
        _bridgeClient = bridgeClient;
        _streamingClient = streamingClient;
        _credentialStore = credentialStore;
        _settingsStore = settingsStore;
        _analysisOptions = analysisOptions;
        _orchestrator = orchestrator;
        _diagnostics = diagnostics;

        Presets = new(presetStore.GetBuiltInPresets());
        EffectModes = new(Enum.GetValues<EffectMode>());
        ChaseDirections = new(Enum.GetValues<ChaseDirection>());
        MovementPatterns = new(Enum.GetValues<MovementPattern>());
        LightPositions = new(Enum.GetValues<LightPosition>());

        RefreshDevicesCommand = new AsyncRelayCommand(() => RunSafeAsync(RefreshDevicesAsync));
        DiscoverBridgesCommand = new AsyncRelayCommand(() => RunSafeAsync(DiscoverBridgesAsync));
        AddManualBridgeCommand = new AsyncRelayCommand(() => RunSafeAsync(AddManualBridgeAsync));
        PairBridgeCommand = new AsyncRelayCommand(() => RunSafeAsync(PairBridgeAsync));
        ConnectBridgeCommand = new AsyncRelayCommand(() => RunSafeAsync(ConnectBridgeAsync));
        StartAudioCommand = new AsyncRelayCommand(() => RunSafeAsync(StartAudioAsync));
        StopAudioCommand = new AsyncRelayCommand(() => RunSafeAsync(StopAudioAsync));
        StartShowCommand = new AsyncRelayCommand(() => RunSafeAsync(StartShowAsync));
        StopShowCommand = new AsyncRelayCommand(() => RunSafeAsync(StopShowAsync));
        ApplyPresetCommand = new RelayCommand(ApplySelectedPreset);
        MoveLightUpCommand = new RelayCommand(() => MoveSelectedLight(-1));
        MoveLightDownCommand = new RelayCommand(() => MoveSelectedLight(1));
        AddPaletteColorCommand = new RelayCommand(AddPaletteColor);
        RemovePaletteColorCommand = new RelayCommand(RemovePaletteColor);
        SaveSettingsCommand = new AsyncRelayCommand(() => RunSafeAsync(SaveSettingsAsync));
    }

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = [];
    public ObservableCollection<HueBridgeInfo> Bridges { get; } = [];
    public ObservableCollection<LightItemViewModel> Lights { get; } = [];
    public ObservableCollection<EntertainmentConfiguration> EntertainmentConfigurations { get; } = [];
    public ObservableCollection<PresetDefinition> Presets { get; }
    public ObservableCollection<ColorSwatchViewModel> PaletteSwatches { get; } = [];
    public ObservableCollection<DiagnosticEntry> Logs { get; } = [];
    public ObservableCollection<EffectMode> EffectModes { get; }
    public ObservableCollection<ChaseDirection> ChaseDirections { get; }
    public ObservableCollection<MovementPattern> MovementPatterns { get; }
    public ObservableCollection<LightPosition> LightPositions { get; }

    public ICommand RefreshDevicesCommand { get; }
    public ICommand DiscoverBridgesCommand { get; }
    public ICommand AddManualBridgeCommand { get; }
    public ICommand PairBridgeCommand { get; }
    public ICommand ConnectBridgeCommand { get; }
    public ICommand StartAudioCommand { get; }
    public ICommand StopAudioCommand { get; }
    public ICommand StartShowCommand { get; }
    public ICommand StopShowCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand MoveLightUpCommand { get; }
    public ICommand MoveLightDownCommand { get; }
    public ICommand AddPaletteColorCommand { get; }
    public ICommand RemovePaletteColorCommand { get; }
    public ICommand SaveSettingsCommand { get; }

    public AudioDeviceInfo? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set => SetProperty(ref _selectedAudioDevice, value);
    }

    public HueBridgeInfo? SelectedBridge
    {
        get => _selectedBridge;
        set => SetProperty(ref _selectedBridge, value);
    }

    public EntertainmentConfiguration? SelectedEntertainmentConfiguration
    {
        get => _selectedEntertainmentConfiguration;
        set => SetProperty(ref _selectedEntertainmentConfiguration, value);
    }

    public PresetDefinition? SelectedPreset
    {
        get => _selectedPreset;
        set => SetProperty(ref _selectedPreset, value);
    }

    public LightItemViewModel? SelectedLight
    {
        get => _selectedLight;
        set => SetProperty(ref _selectedLight, value);
    }

    public ColorSwatchViewModel? SelectedSwatch
    {
        get => _selectedSwatch;
        set => SetProperty(ref _selectedSwatch, value);
    }

    public string ManualBridgeAddress
    {
        get => _manualBridgeAddress;
        set => SetProperty(ref _manualBridgeAddress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string AudioStatus
    {
        get => _audioStatus;
        private set => SetProperty(ref _audioStatus, value);
    }

    public string BridgeStatus
    {
        get => _bridgeStatus;
        private set => SetProperty(ref _bridgeStatus, value);
    }

    public string BridgeMode
    {
        get => _bridgeMode;
        private set => SetProperty(ref _bridgeMode, value);
    }

    public string CurrentInput
    {
        get => _currentInput;
        private set => SetProperty(ref _currentInput, value);
    }

    public string LastBeat
    {
        get => _lastBeat;
        private set => SetProperty(ref _lastBeat, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsShowRunning
    {
        get => _isShowRunning;
        private set => SetProperty(ref _isShowRunning, value);
    }

    public bool UseEntertainment
    {
        get => _useEntertainment;
        set => SetProperty(ref _useEntertainment, value);
    }

    public double InputLevel
    {
        get => _inputLevel;
        private set => SetProperty(ref _inputLevel, value);
    }

    public double BassActivity
    {
        get => _bassActivity;
        private set => SetProperty(ref _bassActivity, value);
    }

    public double SubBassLevel
    {
        get => _subBassLevel;
        private set => SetProperty(ref _subBassLevel, value);
    }

    public double LowMidsLevel
    {
        get => _lowMidsLevel;
        private set => SetProperty(ref _lowMidsLevel, value);
    }

    public double MidsLevel
    {
        get => _midsLevel;
        private set => SetProperty(ref _midsLevel, value);
    }

    public double HighsLevel
    {
        get => _highsLevel;
        private set => SetProperty(ref _highsLevel, value);
    }

    public float[] Spectrum
    {
        get => _spectrum;
        private set => SetProperty(ref _spectrum, value);
    }

    public int HueUpdateRate
    {
        get => _hueUpdateRate;
        private set => SetProperty(ref _hueUpdateRate, value);
    }

    public long DroppedAudioChunks
    {
        get => _droppedAudioChunks;
        private set => SetProperty(ref _droppedAudioChunks, value);
    }

    public long FailedHueFrames
    {
        get => _failedHueFrames;
        private set => SetProperty(ref _failedHueFrames, value);
    }

    public int SelectedLightCount => Lights.Count(static light => light.IsSelected);

    public string CurrentEffect => _settings.Effect.Mode.ToString();

    public EffectMode SelectedEffectMode
    {
        get => _settings.Effect.Mode;
        set
        {
            if (_settings.Effect.Mode == value)
            {
                return;
            }

            _settings.Effect.Mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentEffect));
        }
    }

    public ChaseDirection SelectedChaseDirection
    {
        get => _settings.Effect.ChaseDirection;
        set
        {
            _settings.Effect.ChaseDirection = value;
            OnPropertyChanged();
        }
    }

    public MovementPattern SelectedMovementPattern
    {
        get => _settings.Effect.MovementPattern;
        set
        {
            _settings.Effect.MovementPattern = value;
            OnPropertyChanged();
        }
    }

    public double BassSensitivity
    {
        get => _settings.Effect.BassSensitivity;
        set
        {
            _settings.Effect.BassSensitivity = (float)value;
            _analysisOptions.BassSensitivity = (float)value;
            OnPropertyChanged();
        }
    }

    public double MidSensitivity
    {
        get => _settings.Effect.MidSensitivity;
        set
        {
            _settings.Effect.MidSensitivity = (float)value;
            OnPropertyChanged();
        }
    }

    public double TrebleSensitivity
    {
        get => _settings.Effect.TrebleSensitivity;
        set
        {
            _settings.Effect.TrebleSensitivity = (float)value;
            OnPropertyChanged();
        }
    }

    public double BrightnessLimit
    {
        get => _settings.Effect.BrightnessLimit;
        set
        {
            _settings.Effect.BrightnessLimit = (float)value;
            OnPropertyChanged();
        }
    }

    public double SaturationLimit
    {
        get => _settings.Effect.SaturationLimit;
        set
        {
            _settings.Effect.SaturationLimit = (float)value;
            OnPropertyChanged();
        }
    }

    public double MaximumFlashIntensity
    {
        get => _settings.Effect.MaximumFlashIntensity;
        set
        {
            _settings.Effect.MaximumFlashIntensity = (float)value;
            OnPropertyChanged();
        }
    }

    public int AttackMs
    {
        get => _settings.Effect.AttackMs;
        set
        {
            _settings.Effect.AttackMs = value;
            OnPropertyChanged();
        }
    }

    public int ReleaseMs
    {
        get => _settings.Effect.ReleaseMs;
        set
        {
            _settings.Effect.ReleaseMs = value;
            OnPropertyChanged();
        }
    }

    public bool StrobeSafe
    {
        get => _settings.Effect.StrobeSafe;
        set
        {
            _settings.Effect.StrobeSafe = value;
            OnPropertyChanged();
        }
    }

    public bool Monochrome
    {
        get => _settings.Effect.Monochrome;
        set
        {
            _settings.Effect.Monochrome = value;
            OnPropertyChanged();
        }
    }

    public double InputGain
    {
        get => _settings.Audio.Gain;
        set
        {
            _settings.Audio.Gain = (float)value;
            OnPropertyChanged();
        }
    }

    public double NoiseGateDb
    {
        get => _settings.Audio.NoiseGateDb;
        set
        {
            _settings.Audio.NoiseGateDb = (float)value;
            OnPropertyChanged();
        }
    }

    public int LatencyCompensationMs
    {
        get => _settings.Audio.LatencyCompensationMs;
        set
        {
            _settings.Audio.LatencyCompensationMs = value;
            OnPropertyChanged();
        }
    }

    public int CompatibilityUpdateRate
    {
        get => _settings.Hue.CompatibilityUpdatesPerSecond;
        set
        {
            _settings.Hue.CompatibilityUpdatesPerSecond = value;
            OnPropertyChanged();
        }
    }

    public int EntertainmentUpdateRate
    {
        get => _settings.Hue.EntertainmentUpdatesPerSecond;
        set
        {
            _settings.Hue.EntertainmentUpdatesPerSecond = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        _orchestrator.AnalysisFrameAvailable += OnAnalysisFrame;
        _orchestrator.PipelineFailed += OnPipelineFailed;
        _orchestrator.OutputStatusChanged += OnOutputStatusChanged;
        _diagnostics.EntryAdded += OnDiagnosticEntry;
        foreach (var entry in _diagnostics.Entries.TakeLast(500))
        {
            Logs.Add(entry);
        }

        await _orchestrator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        _settings = await _settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        CopyAnalysisSettings();
        RebuildPalette();
        RaiseSettingsProperties();
        await RefreshDevicesAsync().ConfigureAwait(true);

        SelectedPreset = Presets.FirstOrDefault(preset =>
            string.Equals(preset.Name, _settings.ActivePreset, StringComparison.Ordinal)) ??
                         Presets.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(_settings.Hue.BridgeIpAddress))
        {
            try
            {
                var bridge = await _bridgeDiscovery.ResolveManualAsync(
                    _settings.Hue.BridgeIpAddress,
                    CancellationToken.None).ConfigureAwait(true);
                Bridges.Add(bridge);
                SelectedBridge = bridge;
                await ConnectBridgeAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or HueApiException or InvalidOperationException)
            {
                StatusMessage = "Saved bridge is currently unavailable";
            }
        }
    }

    public async Task ShutdownAsync()
    {
        SyncSettingsFromView();
        await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        _orchestrator.AnalysisFrameAvailable -= OnAnalysisFrame;
        _orchestrator.PipelineFailed -= OnPipelineFailed;
        _orchestrator.OutputStatusChanged -= OnOutputStatusChanged;
        _diagnostics.EntryAdded -= OnDiagnosticEntry;
    }

    private async Task RefreshDevicesAsync()
    {
        var selectedId = SelectedAudioDevice?.Id ?? _settings.AudioDeviceId;
        var devices = await _audioDevices.GetDevicesAsync(CancellationToken.None).ConfigureAwait(true);
        AudioDevices.Clear();
        foreach (var device in devices)
        {
            AudioDevices.Add(device);
        }

        SelectedAudioDevice = AudioDevices.FirstOrDefault(device =>
                                  string.Equals(device.Id, selectedId, StringComparison.Ordinal)) ??
                              AudioDevices.FirstOrDefault(device =>
                                  device.Kind == _settings.AudioInputKind && device.IsDefault) ??
                              AudioDevices.FirstOrDefault();
        StatusMessage = AudioDevices.Count == 0
            ? "No active Windows audio endpoints found"
            : $"Found {AudioDevices.Count} audio input option(s)";
    }

    private async Task DiscoverBridgesAsync()
    {
        StatusMessage = "Searching the local network for Hue Bridges…";
        var bridges = await _bridgeDiscovery.DiscoverAsync(
            TimeSpan.FromSeconds(4),
            CancellationToken.None).ConfigureAwait(true);
        Bridges.Clear();
        foreach (var bridge in bridges)
        {
            Bridges.Add(bridge);
        }

        SelectedBridge = Bridges.FirstOrDefault();
        StatusMessage = bridges.Count == 0
            ? "No bridge found; enter its IP address below"
            : $"Found {bridges.Count} Hue Bridge(s)";
    }

    private async Task AddManualBridgeAsync()
    {
        var bridge = await _bridgeDiscovery.ResolveManualAsync(
            ManualBridgeAddress,
            CancellationToken.None).ConfigureAwait(true);
        var existing = Bridges.FirstOrDefault(item =>
            string.Equals(item.Id, bridge.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Bridges.Add(bridge);
            SelectedBridge = bridge;
        }
        else
        {
            SelectedBridge = existing;
        }

        StatusMessage = $"Found {bridge.Name}";
    }

    private async Task PairBridgeAsync()
    {
        var bridge = SelectedBridge ??
                     throw new InvalidOperationException("Select or add a Hue Bridge first.");
        StatusMessage = "Pairing: press the bridge link button now…";
        _credentials = await _authenticator.PairAsync(
            bridge,
            "RhythmLume",
            Environment.MachineName,
            true,
            CancellationToken.None).ConfigureAwait(true);
        await _credentialStore.SaveAsync(
            bridge.Id,
            _credentials,
            CancellationToken.None).ConfigureAwait(true);
        await ConnectBridgeCoreAsync(bridge, _credentials).ConfigureAwait(true);
    }

    private async Task ConnectBridgeAsync()
    {
        var bridge = SelectedBridge ??
                     throw new InvalidOperationException("Select or add a Hue Bridge first.");
        var credentials = await _credentialStore.LoadAsync(
            bridge.Id,
            CancellationToken.None).ConfigureAwait(true);
        if (credentials is null)
        {
            throw new InvalidOperationException(
                "No saved credentials exist for this bridge. Press its link button and choose Pair.");
        }

        _credentials = credentials;
        await ConnectBridgeCoreAsync(bridge, credentials).ConfigureAwait(true);
    }

    private async Task ConnectBridgeCoreAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials)
    {
        StatusMessage = "Connecting to Hue Bridge…";
        _capabilities = await _capabilityDetector.DetectAsync(
            bridge,
            credentials,
            CancellationToken.None).ConfigureAwait(true);
        await _bridgeClient.ConnectAsync(
            bridge,
            credentials,
            CancellationToken.None).ConfigureAwait(true);
        var lights = await _bridgeClient.GetLightsAsync(CancellationToken.None).ConfigureAwait(true);
        PopulateLights(lights);

        EntertainmentConfigurations.Clear();
        if (_capabilities.SupportsEntertainment)
        {
            try
            {
                var configurations = await _streamingClient.GetConfigurationsAsync(
                    bridge,
                    credentials,
                    CancellationToken.None).ConfigureAwait(true);
                foreach (var configuration in configurations)
                {
                    EntertainmentConfigurations.Add(configuration);
                }

                SelectedEntertainmentConfiguration =
                    EntertainmentConfigurations.FirstOrDefault(configuration =>
                        string.Equals(
                            configuration.Id,
                            _settings.Hue.EntertainmentConfigurationId,
                            StringComparison.Ordinal)) ??
                    EntertainmentConfigurations.FirstOrDefault();
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or HueApiException)
            {
                StatusMessage = "Bridge connected; Entertainment areas could not be loaded";
            }
        }

        BridgeStatus = bridge.Name;
        BridgeMode = _capabilities.SupportsEntertainment &&
                     EntertainmentConfigurations.Count > 0
            ? "Fast / Entertainment ready"
            : "Compatibility / REST";
        StatusMessage = _capabilities.Warning ?? $"Connected with {lights.Count} light(s)";
        _settings.Hue.BridgeId = bridge.Id;
        _settings.Hue.BridgeIpAddress = bridge.InternalIpAddress;
        OnPropertyChanged(nameof(SelectedLightCount));
    }

    private async Task StartAudioAsync()
    {
        var device = SelectedAudioDevice ??
                     throw new InvalidOperationException("Select an audio input first.");
        await _orchestrator.StartAudioAsync(
            device,
            _settings.Audio,
            CancellationToken.None).ConfigureAwait(true);
        _settings.AudioDeviceId = device.Id;
        _settings.AudioInputKind = device.Kind;
        AudioStatus = "Capturing";
        CurrentInput = device.Name;
        StatusMessage = "Audio analysis is live";
    }

    private async Task StopAudioAsync()
    {
        await _orchestrator.StopAudioAsync(CancellationToken.None).ConfigureAwait(true);
        AudioStatus = "Stopped";
        CurrentInput = "No audio input";
        IsShowRunning = false;
        StatusMessage = "Audio capture stopped";
    }

    private async Task StartShowAsync()
    {
        if (!_orchestrator.IsAudioRunning)
        {
            await StartAudioAsync().ConfigureAwait(true);
        }

        var bridge = SelectedBridge ??
                     throw new InvalidOperationException("Connect to a Hue Bridge first.");
        var credentials = _credentials ??
                          throw new InvalidOperationException("Pair with the Hue Bridge first.");
        SyncPalette();
        var selected = OrderSelectedLights();
        await _orchestrator.StartShowAsync(
            selected,
            _settings.Effect,
            bridge,
            credentials,
            SelectedEntertainmentConfiguration,
            UseEntertainment && _capabilities?.SupportsEntertainment == true,
            _settings.Hue.CompatibilityUpdatesPerSecond,
            _settings.Hue.EntertainmentUpdatesPerSecond,
            CancellationToken.None).ConfigureAwait(true);
        IsShowRunning = true;
        StatusMessage = $"{CurrentEffect} is running";
    }

    private async Task StopShowAsync()
    {
        await _orchestrator.StopShowAsync(CancellationToken.None).ConfigureAwait(true);
        IsShowRunning = false;
        HueUpdateRate = 0;
        StatusMessage = "Light show stopped";
    }

    private async Task SaveSettingsAsync()
    {
        SyncSettingsFromView();
        await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(true);
        StatusMessage = "Settings saved";
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (HueLinkButtonRequiredException exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Bridge link button not detected";
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Action needs attention";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySelectedPreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        _settings.ActivePreset = SelectedPreset.Name;
        _settings.Effect = CloneEffect(SelectedPreset.Effect);
        _settings.Hue.CompatibilityUpdatesPerSecond =
            SelectedPreset.CompatibilityUpdatesPerSecond;
        _settings.Hue.EntertainmentUpdatesPerSecond =
            SelectedPreset.EntertainmentUpdatesPerSecond;
        _analysisOptions.BassSensitivity = _settings.Effect.BassSensitivity;
        RebuildPalette();
        RaiseSettingsProperties();
        StatusMessage = $"Applied {SelectedPreset.Name}";
    }

    private void PopulateLights(IReadOnlyList<HueLight> lights)
    {
        Lights.Clear();
        foreach (var light in lights)
        {
            var item = new LightItemViewModel(light)
            {
                IsSelected = _settings.SelectedLightIds.Contains(light.Id, StringComparer.Ordinal),
                Position = _settings.LightPositions.GetValueOrDefault(light.Id),
                Order = GetSavedOrder(light.Id, light.Order)
            };
            item.SelectionChanged += OnLightSelectionChanged;
            Lights.Add(item);
        }
    }

    private int GetSavedOrder(string lightId, int fallback)
    {
        var index = _settings.CustomLightOrder.FindIndex(id =>
            string.Equals(id, lightId, StringComparison.Ordinal));
        return index < 0 ? fallback : index;
    }

    private IReadOnlyList<HueLight> OrderSelectedLights()
    {
        var selected = Lights.Where(static light => light.IsSelected && light.IsReachable);
        selected = _settings.Effect.MovementPattern switch
        {
            MovementPattern.LeftToRight => selected.OrderBy(static light =>
                light.Position switch
                {
                    LightPosition.Left => 0,
                    LightPosition.Center => 1,
                    LightPosition.Right => 2,
                    _ => 3
                }).ThenBy(static light => light.Order),
            MovementPattern.FrontToBack => selected.OrderBy(static light =>
                light.Position switch
                {
                    LightPosition.Front => 0,
                    LightPosition.Center => 1,
                    LightPosition.Back => 2,
                    _ => 3
                }).ThenBy(static light => light.Order),
            _ => selected.OrderBy(static light => light.Order)
        };
        return selected.Select(static light => light.ToDomain()).ToArray();
    }

    private void MoveSelectedLight(int offset)
    {
        if (SelectedLight is null)
        {
            return;
        }

        var ordered = Lights.OrderBy(static light => light.Order).ToList();
        var index = ordered.IndexOf(SelectedLight);
        var target = Math.Clamp(index + offset, 0, ordered.Count - 1);
        if (index == target)
        {
            return;
        }

        (ordered[index].Order, ordered[target].Order) =
            (ordered[target].Order, ordered[index].Order);
        OnLightSelectionChanged();
    }

    private void AddPaletteColor()
    {
        var swatch = new ColorSwatchViewModel(new(0.15f, 0.65f, 0.95f));
        swatch.ColorChanged += SyncPalette;
        PaletteSwatches.Add(swatch);
        SelectedSwatch = swatch;
        SyncPalette();
    }

    private void RemovePaletteColor()
    {
        if (SelectedSwatch is null || PaletteSwatches.Count <= 1)
        {
            return;
        }

        PaletteSwatches.Remove(SelectedSwatch);
        SelectedSwatch = PaletteSwatches.FirstOrDefault();
        SyncPalette();
    }

    private void RebuildPalette()
    {
        PaletteSwatches.Clear();
        foreach (var color in _settings.Effect.Palette)
        {
            var swatch = new ColorSwatchViewModel(color);
            swatch.ColorChanged += SyncPalette;
            PaletteSwatches.Add(swatch);
        }

        if (PaletteSwatches.Count == 0)
        {
            AddPaletteColor();
        }

        SelectedSwatch = PaletteSwatches.FirstOrDefault();
    }

    private void SyncPalette() =>
        _settings.Effect.Palette = PaletteSwatches.Select(static swatch => swatch.ToColor()).ToList();

    private void SyncSettingsFromView()
    {
        SyncPalette();
        _settings.AudioDeviceId = SelectedAudioDevice?.Id;
        if (SelectedAudioDevice is not null)
        {
            _settings.AudioInputKind = SelectedAudioDevice.Kind;
        }

        _settings.SelectedLightIds = Lights
            .Where(static light => light.IsSelected)
            .Select(static light => light.Id)
            .ToList();
        _settings.LightPositions = Lights.ToDictionary(
            static light => light.Id,
            static light => light.Position,
            StringComparer.Ordinal);
        _settings.CustomLightOrder = Lights
            .OrderBy(static light => light.Order)
            .Select(static light => light.Id)
            .ToList();
        _settings.Hue.EntertainmentConfigurationId =
            SelectedEntertainmentConfiguration?.Id;
    }

    private void CopyAnalysisSettings()
    {
        _analysisOptions.FftSize = _settings.Analysis.FftSize;
        _analysisOptions.HopSize = _settings.Analysis.HopSize;
        _analysisOptions.BassSensitivity = _settings.Effect.BassSensitivity;
        _analysisOptions.NoiseFloor = _settings.Analysis.NoiseFloor;
        _analysisOptions.BeatCooldownMs = _settings.Analysis.BeatCooldownMs;
        _analysisOptions.AttackMs = _settings.Analysis.AttackMs;
        _analysisOptions.ReleaseMs = _settings.Analysis.ReleaseMs;
        _useEntertainment = _settings.Hue.PreferredMode == HueControlMode.Entertainment;
    }

    private void RaiseSettingsProperties()
    {
        OnPropertyChanged(nameof(SelectedEffectMode));
        OnPropertyChanged(nameof(SelectedChaseDirection));
        OnPropertyChanged(nameof(SelectedMovementPattern));
        OnPropertyChanged(nameof(BassSensitivity));
        OnPropertyChanged(nameof(MidSensitivity));
        OnPropertyChanged(nameof(TrebleSensitivity));
        OnPropertyChanged(nameof(BrightnessLimit));
        OnPropertyChanged(nameof(SaturationLimit));
        OnPropertyChanged(nameof(MaximumFlashIntensity));
        OnPropertyChanged(nameof(AttackMs));
        OnPropertyChanged(nameof(ReleaseMs));
        OnPropertyChanged(nameof(StrobeSafe));
        OnPropertyChanged(nameof(Monochrome));
        OnPropertyChanged(nameof(InputGain));
        OnPropertyChanged(nameof(NoiseGateDb));
        OnPropertyChanged(nameof(LatencyCompensationMs));
        OnPropertyChanged(nameof(CompatibilityUpdateRate));
        OnPropertyChanged(nameof(EntertainmentUpdateRate));
        OnPropertyChanged(nameof(UseEntertainment));
        OnPropertyChanged(nameof(CurrentEffect));
    }

    private static EffectSettings CloneEffect(EffectSettings source) => new()
    {
        Mode = source.Mode,
        ChaseDirection = source.ChaseDirection,
        MovementPattern = source.MovementPattern,
        BassSensitivity = source.BassSensitivity,
        MidSensitivity = source.MidSensitivity,
        TrebleSensitivity = source.TrebleSensitivity,
        AttackMs = source.AttackMs,
        ReleaseMs = source.ReleaseMs,
        BrightnessLimit = source.BrightnessLimit,
        SaturationLimit = source.SaturationLimit,
        Monochrome = source.Monochrome,
        StrobeSafe = source.StrobeSafe,
        MaximumFlashIntensity = source.MaximumFlashIntensity,
        Palette = source.Palette.ToList()
    };

    private void OnLightSelectionChanged()
    {
        SyncSettingsFromView();
        OnPropertyChanged(nameof(SelectedLightCount));
    }

    private void OnAnalysisFrame(AnalysisFrame frame)
    {
        _latestFrame = frame;
        if (Interlocked.Exchange(ref _uiUpdateQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var latest = _latestFrame;
            if (latest is not null)
            {
                InputLevel = latest.InputLevel * 100;
                SubBassLevel = latest.NormalizedBandEnergy.SubBass * 100;
                BassActivity = latest.NormalizedBandEnergy.Bass * 100;
                LowMidsLevel = latest.NormalizedBandEnergy.LowMids * 100;
                MidsLevel = latest.NormalizedBandEnergy.Mids * 100;
                HighsLevel = latest.NormalizedBandEnergy.Highs * 100;
                Spectrum = latest.Spectrum.ToArray();
                if (latest.BassHit is not null)
                {
                    LastBeat =
                        $"Kick {latest.BassHit.Strength:P0} · confidence {latest.BassHit.Confidence:P0}";
                }

                DroppedAudioChunks = _orchestrator.DroppedAudioChunks;
                FailedHueFrames = _orchestrator.FailedFrames;
            }

            Interlocked.Exchange(ref _uiUpdateQueued, 0);
        });
    }

    private void OnPipelineFailed(Exception exception)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ErrorMessage = exception.Message;
            StatusMessage = "The live pipeline encountered an error";
        });
    }

    private void OnOutputStatusChanged(HueControlMode mode, int rate)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            BridgeMode = mode switch
            {
                HueControlMode.Entertainment => "Fast / Entertainment",
                HueControlMode.Compatibility => "Compatibility / REST",
                _ => "Disconnected"
            };
            HueUpdateRate = rate;
        });
    }

    private void OnDiagnosticEntry(DiagnosticEntry entry)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Logs.Add(entry);
            while (Logs.Count > 500)
            {
                Logs.RemoveAt(0);
            }
        });
    }
}
