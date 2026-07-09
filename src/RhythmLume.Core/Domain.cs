using System.Collections.ObjectModel;

namespace RhythmLume.Core;

public enum AudioInputKind
{
    SystemPlayback,
    Microphone
}

public enum FrequencyBand
{
    SubBass,
    Bass,
    LowMids,
    Mids,
    Highs
}

public enum EffectMode
{
    BassChase,
    BassPulse,
    FrequencySplit,
    EnergyWave,
    CalmAmbient
}

public enum ChaseDirection
{
    Forward,
    Reverse,
    Bounce,
    Alternating,
    CenterOut
}

public enum MovementPattern
{
    CustomOrder,
    LeftToRight,
    FrontToBack,
    Circular,
    AlternatingSides,
    CenterOut
}

public enum LightPosition
{
    Unassigned,
    Left,
    Right,
    Front,
    Back,
    Center,
    Floor,
    Ceiling
}

public enum HueBridgeGeneration
{
    Unknown,
    Generation1,
    Generation2,
    BridgePro
}

public enum HueControlMode
{
    Disconnected,
    Compatibility,
    Entertainment
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}

public readonly record struct RgbColor(float Red, float Green, float Blue)
{
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor White = new(1, 1, 1);

    public RgbColor Clamp(float saturationLimit = 1)
    {
        var r = Math.Clamp(Red, 0, 1);
        var g = Math.Clamp(Green, 0, 1);
        var b = Math.Clamp(Blue, 0, 1);
        var gray = (r + g + b) / 3f;
        var saturation = Math.Clamp(saturationLimit, 0, 1);
        return new(
            gray + ((r - gray) * saturation),
            gray + ((g - gray) * saturation),
            gray + ((b - gray) * saturation));
    }

    public static RgbColor Lerp(RgbColor from, RgbColor to, float amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return new(
            from.Red + ((to.Red - from.Red) * t),
            from.Green + ((to.Green - from.Green) * t),
            from.Blue + ((to.Blue - from.Blue) * t));
    }
}

public readonly record struct BandValues(
    float SubBass,
    float Bass,
    float LowMids,
    float Mids,
    float Highs)
{
    public float this[FrequencyBand band] => band switch
    {
        FrequencyBand.SubBass => SubBass,
        FrequencyBand.Bass => Bass,
        FrequencyBand.LowMids => LowMids,
        FrequencyBand.Mids => Mids,
        FrequencyBand.Highs => Highs,
        _ => throw new ArgumentOutOfRangeException(nameof(band))
    };

    public float Average => (SubBass + Bass + LowMids + Mids + Highs) / 5f;

    public BandValues Clamp01() => new(
        Math.Clamp(SubBass, 0, 1),
        Math.Clamp(Bass, 0, 1),
        Math.Clamp(LowMids, 0, 1),
        Math.Clamp(Mids, 0, 1),
        Math.Clamp(Highs, 0, 1));
}

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioInputKind Kind,
    bool IsDefault,
    int Channels = 2,
    int SampleRate = 48_000);

public sealed record AudioChunk(
    float[] Samples,
    int SampleRate,
    DateTimeOffset CapturedAt,
    long Sequence);

public sealed record BeatEvent(
    DateTimeOffset Timestamp,
    float Strength,
    float Confidence);

public sealed record AnalysisFrame(
    DateTimeOffset Timestamp,
    TimeSpan AudioTime,
    float InputLevel,
    BandValues RawBandEnergy,
    BandValues NormalizedBandEnergy,
    float OverallEnergy,
    float SpectralFlux,
    BeatEvent? BassHit,
    ReadOnlyMemory<float> Spectrum);

public sealed record HueBridgeInfo(
    string Id,
    string InternalIpAddress,
    string Name,
    string? ModelId = null,
    string? SoftwareVersion = null,
    HueBridgeGeneration Generation = HueBridgeGeneration.Unknown,
    bool IsManual = false);

public sealed record HueCredentials(
    string ApplicationKey,
    string? ClientKey);

public sealed record HueBridgeCapabilities(
    HueBridgeGeneration Generation,
    bool SupportsApiV2,
    bool SupportsEntertainment,
    bool SupportsHttps,
    int RecommendedUpdatesPerSecond,
    string? Warning = null);

public sealed record HueLight(
    string Id,
    string? V1Id,
    string Name,
    string ModelId,
    bool IsReachable,
    bool SupportsColor,
    LightPosition Position = LightPosition.Unassigned,
    int Order = 0);

public sealed record EntertainmentChannel(byte ChannelId, IReadOnlyList<string> LightServiceIds);

public sealed record EntertainmentConfiguration(
    string Id,
    string Name,
    bool IsActive,
    IReadOnlyList<EntertainmentChannel> Channels);

public sealed record LightUpdate(
    string LightId,
    float Brightness,
    RgbColor Color,
    TimeSpan Transition,
    bool TurnOn = true)
{
    public LightUpdate Clamp(float brightnessLimit = 1, float saturationLimit = 1) =>
        this with
        {
            Brightness = Math.Clamp(Brightness, 0, Math.Clamp(brightnessLimit, 0, 1)),
            Color = Color.Clamp(saturationLimit),
            Transition = Transition < TimeSpan.Zero ? TimeSpan.Zero : Transition
        };
}

public sealed class AudioInputOptions
{
    public float Gain { get; set; } = 1f;
    public float NoiseGateDb { get; set; } = -72f;
    public int LatencyCompensationMs { get; set; }
}

public sealed class AnalysisOptions
{
    public int FftSize { get; set; } = 2048;
    public int HopSize { get; set; } = 512;
    public float BassSensitivity { get; set; } = 1f;
    public float NoiseFloor { get; set; } = 0.012f;
    public int BeatCooldownMs { get; set; } = 120;
    public int AttackMs { get; set; } = 25;
    public int ReleaseMs { get; set; } = 180;
}

public sealed class EffectSettings
{
    public EffectMode Mode { get; set; } = EffectMode.BassChase;
    public ChaseDirection ChaseDirection { get; set; } = ChaseDirection.Forward;
    public MovementPattern MovementPattern { get; set; } = MovementPattern.CustomOrder;
    public float BassSensitivity { get; set; } = 1f;
    public float MidSensitivity { get; set; } = 1f;
    public float TrebleSensitivity { get; set; } = 0.75f;
    public int AttackMs { get; set; } = 35;
    public int ReleaseMs { get; set; } = 300;
    public float BrightnessLimit { get; set; } = 0.85f;
    public float SaturationLimit { get; set; } = 0.95f;
    public bool Monochrome { get; set; }
    public bool StrobeSafe { get; set; } = true;
    public float MaximumFlashIntensity { get; set; } = 0.72f;
    public List<RgbColor> Palette { get; set; } =
    [
        new(0.10f, 0.02f, 0.55f),
        new(0.02f, 0.30f, 0.95f),
        new(0.90f, 0.08f, 0.42f),
        new(0.05f, 0.85f, 0.72f)
    ];
}

public sealed class HueSettings
{
    public string? BridgeId { get; set; }
    public string? BridgeIpAddress { get; set; }
    public HueControlMode PreferredMode { get; set; } = HueControlMode.Entertainment;
    public string? EntertainmentConfigurationId { get; set; }
    public int CompatibilityUpdatesPerSecond { get; set; } = 8;
    public int EntertainmentUpdatesPerSecond { get; set; } = 50;
}

public sealed class AppSettings
{
    public string? AudioDeviceId { get; set; }
    public AudioInputKind AudioInputKind { get; set; } = AudioInputKind.SystemPlayback;
    public AudioInputOptions Audio { get; set; } = new();
    public AnalysisOptions Analysis { get; set; } = new();
    public EffectSettings Effect { get; set; } = new();
    public HueSettings Hue { get; set; } = new();
    public List<string> SelectedLightIds { get; set; } = [];
    public Dictionary<string, LightPosition> LightPositions { get; set; } = [];
    public List<string> CustomLightOrder { get; set; } = [];
    public string ActivePreset { get; set; } = "Bass Chase";
}

public sealed record PresetDefinition(
    string Name,
    string Description,
    EffectSettings Effect,
    int CompatibilityUpdatesPerSecond,
    int EntertainmentUpdatesPerSecond);

public sealed record DiagnosticEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message);

public interface IAudioInput : IAsyncDisposable
{
    bool IsRunning { get; }
    AudioDeviceInfo? ActiveDevice { get; }
    event Action<AudioChunk>? SamplesAvailable;
    event Action<Exception>? CaptureFailed;
    Task StartAsync(AudioDeviceInfo device, AudioInputOptions options, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IAudioDeviceService
{
    Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken);
}

public interface IAudioAnalyzer : IAsyncDisposable
{
    event Action<AnalysisFrame>? AnalysisAvailable;
    long DroppedChunks { get; }
    bool TrySubmit(AudioChunk chunk);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IBeatDetector
{
    BeatEvent? Process(
        DateTimeOffset timestamp,
        float bassEnergy,
        float bassFlux,
        float sensitivity);

    void Reset();
}

public interface IHueBridgeDiscovery
{
    Task<IReadOnlyList<HueBridgeInfo>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<HueBridgeInfo> ResolveManualAsync(
        string hostOrAddress,
        CancellationToken cancellationToken);
}

public interface IHueAuthenticator
{
    Task<HueCredentials> PairAsync(
        HueBridgeInfo bridge,
        string applicationName,
        string deviceName,
        bool requestEntertainmentKey,
        CancellationToken cancellationToken);
}

public interface IHueCapabilityDetector
{
    Task<HueBridgeCapabilities> DetectAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        CancellationToken cancellationToken);
}

public interface IHueBridgeClient : IAsyncDisposable
{
    HueBridgeInfo? Bridge { get; }
    HueControlMode Mode { get; }
    Task ConnectAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HueLight>> GetLightsAsync(CancellationToken cancellationToken);

    Task SendUpdatesAsync(
        IReadOnlyList<LightUpdate> updates,
        CancellationToken cancellationToken);
}

public interface IHueStreamingClient : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<IReadOnlyList<EntertainmentConfiguration>> GetConfigurationsAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        CancellationToken cancellationToken);

    Task ConnectAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        EntertainmentConfiguration configuration,
        CancellationToken cancellationToken);

    Task SendFrameAsync(
        IReadOnlyDictionary<byte, RgbColor> channelColors,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

public interface ILightEffectEngine
{
    EffectMode Mode { get; set; }
    void Reset();
    IReadOnlyList<LightUpdate> Render(
        AnalysisFrame frame,
        IReadOnlyList<HueLight> lights,
        EffectSettings settings);
}

public interface IColorEngine
{
    RgbColor GetColor(
        AnalysisFrame frame,
        int lightIndex,
        int lightCount,
        EffectSettings settings);
}

public interface IPresetStore
{
    IReadOnlyList<PresetDefinition> GetBuiltInPresets();
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface ICredentialStore
{
    Task SaveAsync(string bridgeId, HueCredentials credentials, CancellationToken cancellationToken);
    Task<HueCredentials?> LoadAsync(string bridgeId, CancellationToken cancellationToken);
    Task RemoveAsync(string bridgeId, CancellationToken cancellationToken);
}

public interface IDiagnosticsSink
{
    event Action<DiagnosticEntry>? EntryAdded;
    ReadOnlyCollection<DiagnosticEntry> Entries { get; }
    void Write(string level, string source, string message);
}
