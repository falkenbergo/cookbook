using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RhythmLume.Core;
using RhythmLume.Hue;

namespace RhythmLume.App;

public sealed class MusicOrchestrator : IAsyncDisposable
{
    private readonly IAudioInput _audioInput;
    private readonly IAudioAnalyzer _analyzer;
    private readonly IHueBridgeClient _bridgeClient;
    private readonly IHueStreamingClient _streamingClient;
    private readonly ILightEffectEngine _effectEngine;
    private readonly ILogger<MusicOrchestrator> _logger;
    private readonly Channel<AnalysisFrame> _outputFrames;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _outputWorker;
    private CancellationTokenSource? _showCancellation;
    private IReadOnlyList<HueLight> _lights = [];
    private EntertainmentConfiguration? _configuration;
    private EffectSettings _effectSettings = new();
    private int _targetUpdatesPerSecond = 50;
    private long _sentFrames;
    private long _failedFrames;
    private bool _initialized;

    public MusicOrchestrator(
        IAudioInput audioInput,
        IAudioAnalyzer analyzer,
        IHueBridgeClient bridgeClient,
        IHueStreamingClient streamingClient,
        ILightEffectEngine effectEngine,
        ILogger<MusicOrchestrator> logger)
    {
        _audioInput = audioInput;
        _analyzer = analyzer;
        _bridgeClient = bridgeClient;
        _streamingClient = streamingClient;
        _effectEngine = effectEngine;
        _logger = logger;
        _outputFrames = Channel.CreateBounded<AnalysisFrame>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    }

    public event Action<AnalysisFrame>? AnalysisFrameAvailable;

    public event Action<Exception>? PipelineFailed;

    public event Action<HueControlMode, int>? OutputStatusChanged;

    public bool IsAudioRunning => _audioInput.IsRunning;

    public bool IsShowRunning => _outputWorker is not null;

    public HueControlMode OutputMode { get; private set; } = HueControlMode.Disconnected;

    public long SentFrames => Interlocked.Read(ref _sentFrames);

    public long FailedFrames => Interlocked.Read(ref _failedFrames);

    public long DroppedAudioChunks => _analyzer.DroppedChunks;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _audioInput.SamplesAvailable += OnSamplesAvailable;
        _audioInput.CaptureFailed += OnPipelineFailed;
        _analyzer.AnalysisAvailable += OnAnalysisAvailable;
        await _analyzer.StartAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async Task StartAudioAsync(
        AudioDeviceInfo device,
        AudioInputOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        if (_audioInput.IsRunning)
        {
            await _audioInput.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audioInput.StartAsync(device, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAudioAsync(CancellationToken cancellationToken)
    {
        await StopShowAsync(cancellationToken).ConfigureAwait(false);
        await _audioInput.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartShowAsync(
        IReadOnlyList<HueLight> selectedLights,
        EffectSettings effectSettings,
        HueBridgeInfo bridge,
        HueCredentials credentials,
        EntertainmentConfiguration? entertainmentConfiguration,
        bool preferEntertainment,
        int compatibilityUpdatesPerSecond,
        int entertainmentUpdatesPerSecond,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedLights);
        ArgumentNullException.ThrowIfNull(effectSettings);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(credentials);
        if (selectedLights.Count == 0)
        {
            throw new InvalidOperationException("Select at least one reachable light.");
        }

        await StopShowAsync(cancellationToken).ConfigureAwait(false);
        _lights = selectedLights;
        _effectSettings = effectSettings;
        _configuration = entertainmentConfiguration;
        _effectEngine.Mode = effectSettings.Mode;
        _effectEngine.Reset();
        Interlocked.Exchange(ref _sentFrames, 0);
        Interlocked.Exchange(ref _failedFrames, 0);

        if (preferEntertainment &&
            entertainmentConfiguration is not null &&
            !string.IsNullOrWhiteSpace(credentials.ClientKey))
        {
            try
            {
                await _streamingClient.ConnectAsync(
                    bridge,
                    credentials,
                    entertainmentConfiguration,
                    cancellationToken).ConfigureAwait(false);
                OutputMode = HueControlMode.Entertainment;
                _targetUpdatesPerSecond = Math.Clamp(entertainmentUpdatesPerSecond, 25, 60);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or TimeoutException or HueApiException)
            {
                _logger.LogWarning(
                    exception,
                    "Entertainment streaming was unavailable; using compatibility mode.");
                OutputMode = HueControlMode.Compatibility;
                _targetUpdatesPerSecond = Math.Clamp(compatibilityUpdatesPerSecond, 1, 10);
            }
        }
        else
        {
            OutputMode = HueControlMode.Compatibility;
            _targetUpdatesPerSecond = Math.Clamp(compatibilityUpdatesPerSecond, 1, 10);
        }

        _showCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _outputWorker = Task.Run(
            () => RunOutputAsync(_showCancellation.Token),
            CancellationToken.None);
        OutputStatusChanged?.Invoke(OutputMode, _targetUpdatesPerSecond);
        _logger.LogInformation(
            "Started {Effect} with {LightCount} lights in {Mode} mode.",
            effectSettings.Mode,
            selectedLights.Count,
            OutputMode);
    }

    public async Task StopShowAsync(CancellationToken cancellationToken)
    {
        if (_outputWorker is not null)
        {
            await _showCancellation!.CancelAsync().ConfigureAwait(false);
            try
            {
                await _outputWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_showCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                _showCancellation.Dispose();
                _showCancellation = null;
                _outputWorker = null;
            }
        }

        if (_streamingClient.IsConnected)
        {
            await _streamingClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        OutputMode = _bridgeClient.Mode;
        OutputStatusChanged?.Invoke(OutputMode, 0);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await StopAudioAsync(timeout.Token).ConfigureAwait(false);
            await _analyzer.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _audioInput.SamplesAvailable -= OnSamplesAvailable;
        _audioInput.CaptureFailed -= OnPipelineFailed;
        _analyzer.AnalysisAvailable -= OnAnalysisAvailable;
        await _streamingClient.DisposeAsync().ConfigureAwait(false);
        await _bridgeClient.DisposeAsync().ConfigureAwait(false);
        await _audioInput.DisposeAsync().ConfigureAwait(false);
        await _analyzer.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RunOutputAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1d / _targetUpdatesPerSecond);
        var nextSend = Stopwatch.GetTimestamp();
        var reportStarted = Stopwatch.GetTimestamp();
        var reportCount = 0;
        try
        {
            await foreach (var frame in _outputFrames.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                var latest = frame;
                while (_outputFrames.Reader.TryRead(out var newer))
                {
                    latest = newer;
                }

                var now = Stopwatch.GetTimestamp();
                if (nextSend > now)
                {
                    var delay = TimeSpan.FromSeconds(
                        (nextSend - now) / (double)Stopwatch.Frequency);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                var updates = _effectEngine.Render(latest, _lights, _effectSettings);
                try
                {
                    if (OutputMode == HueControlMode.Entertainment && _streamingClient.IsConnected)
                    {
                        var channels = MapChannels(updates, _configuration!);
                        await _streamingClient.SendFrameAsync(channels, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _bridgeClient.SendUpdatesAsync(updates, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    Interlocked.Increment(ref _sentFrames);
                    reportCount++;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or HueApiException)
                {
                    Interlocked.Increment(ref _failedFrames);
                    _logger.LogWarning(exception, "A Hue output frame failed.");
                }

                nextSend = Stopwatch.GetTimestamp() +
                           (long)(interval.TotalSeconds * Stopwatch.Frequency);
                var reportElapsed = Stopwatch.GetElapsedTime(reportStarted);
                if (reportElapsed >= TimeSpan.FromSeconds(5))
                {
                    _logger.LogDebug(
                        "Effect output rate {Rate:F1} fps; analyzer dropped {Dropped} chunks; failures {Failures}.",
                        reportCount / reportElapsed.TotalSeconds,
                        _analyzer.DroppedChunks,
                        FailedFrames);
                    reportStarted = Stopwatch.GetTimestamp();
                    reportCount = 0;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Hue output worker stopped unexpectedly.");
            PipelineFailed?.Invoke(exception);
        }
    }

    private static IReadOnlyDictionary<byte, RgbColor> MapChannels(
        IReadOnlyList<LightUpdate> updates,
        EntertainmentConfiguration configuration)
    {
        var byLight = updates.ToDictionary(
            static update => update.LightId,
            StringComparer.Ordinal);
        var result = new Dictionary<byte, RgbColor>();
        var hasMembershipData = configuration.Channels.Any(
            static channel => channel.LightServiceIds.Count > 0);
        for (var index = 0; index < configuration.Channels.Count; index++)
        {
            var channel = configuration.Channels[index];
            var update = channel.LightServiceIds
                .Select(id => byLight.GetValueOrDefault(id))
                .FirstOrDefault(static candidate => candidate is not null);
            if (update is null && !hasMembershipData && updates.Count > 0)
            {
                update = updates[index % updates.Count];
            }

            if (update is null)
            {
                result[channel.ChannelId] = RgbColor.Black;
                continue;
            }

            var brightness = Math.Clamp(update.Brightness, 0, 1);
            result[channel.ChannelId] = new(
                update.Color.Red * brightness,
                update.Color.Green * brightness,
                update.Color.Blue * brightness);
        }

        return result;
    }

    private void OnSamplesAvailable(AudioChunk chunk)
    {
        _analyzer.TrySubmit(chunk);
    }

    private void OnAnalysisAvailable(AnalysisFrame frame)
    {
        AnalysisFrameAvailable?.Invoke(frame);
        if (_outputWorker is not null)
        {
            _outputFrames.Writer.TryWrite(frame);
        }
    }

    private void OnPipelineFailed(Exception exception) => PipelineFailed?.Invoke(exception);
}
