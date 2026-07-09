using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RhythmLume.Core;

namespace RhythmLume.Audio.Windows;

public sealed class WindowsAudioDeviceService : IAudioDeviceService
{
    public Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);
        }

        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDeviceInfo>();
        AddDevices(enumerator, DataFlow.Render, AudioInputKind.SystemPlayback, devices);
        AddDevices(enumerator, DataFlow.Capture, AudioInputKind.Microphone, devices);
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    private static void AddDevices(
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        AudioInputKind kind,
        ICollection<AudioDeviceInfo> output)
    {
        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (COMException)
        {
            // No active default endpoint for this flow.
        }

        var endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                output.Add(new(
                    endpoint.ID,
                    endpoint.FriendlyName,
                    kind,
                    string.Equals(endpoint.ID, defaultId, StringComparison.Ordinal),
                    endpoint.AudioClient.MixFormat.Channels,
                    endpoint.AudioClient.MixFormat.SampleRate));
            }
        }
    }
}

public sealed class WasapiAudioInput : IAudioInput
{
    private readonly ILogger<WasapiAudioInput> _logger;
    private readonly object _sync = new();
    private IWaveIn? _capture;
    private MMDevice? _device;
    private AudioInputOptions _options = new();
    private long _sequence;
    private TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WasapiAudioInput(ILogger<WasapiAudioInput> logger)
    {
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public AudioDeviceInfo? ActiveDevice { get; private set; }

    public event Action<AudioChunk>? SamplesAvailable;

    public event Action<Exception>? CaptureFailed;

    public Task StartAsync(
        AudioDeviceInfo device,
        AudioInputOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI audio capture requires Windows.");
        }

        lock (_sync)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("Audio capture is already running.");
            }

            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(device.Id);
            _capture = device.Kind == AudioInputKind.SystemPlayback
                ? new WasapiLoopbackCapture(_device)
                : new WasapiCapture(_device);
            _options = options;
            _sequence = 0;
            _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            ActiveDevice = device;
            IsRunning = true;
        }

        _logger.LogInformation(
            "Started {Kind} capture from {Device} at {SampleRate} Hz.",
            device.Kind,
            device.Name,
            _capture.WaveFormat.SampleRate);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IWaveIn? capture;
        lock (_sync)
        {
            capture = _capture;
            if (!IsRunning || capture is null)
            {
                return;
            }

            IsRunning = false;
            capture.StopRecording();
        }

        try
        {
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out waiting for the audio endpoint to stop.");
        }
        finally
        {
            CleanupCapture(capture);
            _logger.LogInformation("Audio capture stopped.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await StopAsync(timeout.Token).ConfigureAwait(false);
        _device?.Dispose();
        _device = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        var capture = _capture;
        if (!IsRunning || capture is null || eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            var format = capture.WaveFormat;
            var samples = PcmSampleConverter.ToMono(
                eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded),
                format);
            ApplyGainAndGate(samples, _options.Gain, _options.NoiseGateDb);
            var frameDuration = TimeSpan.FromSeconds((double)samples.Length / format.SampleRate);
            var timestamp = DateTimeOffset.UtcNow -
                            frameDuration +
                            TimeSpan.FromMilliseconds(_options.LatencyCompensationMs);
            SamplesAvailable?.Invoke(new(
                samples,
                format.SampleRate,
                timestamp,
                Interlocked.Increment(ref _sequence)));
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            _logger.LogError(exception, "Failed to convert an audio capture buffer.");
            CaptureFailed?.Invoke(exception);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        _stopped.TrySetResult();
        if (eventArgs.Exception is null)
        {
            return;
        }

        _logger.LogError(eventArgs.Exception, "WASAPI capture stopped unexpectedly.");
        CaptureFailed?.Invoke(eventArgs.Exception);
    }

    private void CleanupCapture(IWaveIn capture)
    {
        lock (_sync)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            if (ReferenceEquals(_capture, capture))
            {
                _capture = null;
            }

            _device?.Dispose();
            _device = null;
            ActiveDevice = null;
        }
    }

    private static void ApplyGainAndGate(float[] samples, float gain, float gateDb)
    {
        var safeGain = Math.Clamp(gain, 0, 8);
        var rms = SignalRms(samples);
        var decibels = rms <= 0 ? -120 : 20 * Math.Log10(rms);
        var multiplier = decibels < gateDb ? 0 : safeGain;
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = Math.Clamp(samples[index] * multiplier, -1, 1);
        }
    }

    private static double SignalRms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}

public static class PcmSampleConverter
{
    public static float[] ToMono(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var bytesPerSample = format.BitsPerSample / 8;
        var bytesPerFrame = bytesPerSample * format.Channels;
        if (bytesPerSample <= 0 || format.Channels <= 0 || bytesPerFrame <= 0)
        {
            throw new NotSupportedException($"Invalid capture format: {format}.");
        }

        var frameCount = bytes.Length / bytesPerFrame;
        var mono = new float[frameCount];
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      format is WaveFormatExtensible extensible &&
                      extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
                    format is WaveFormatExtensible pcmExtensible &&
                    pcmExtensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
        if (!isFloat && !isPcm)
        {
            throw new NotSupportedException($"Unsupported WASAPI sample encoding: {format.Encoding}.");
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            var frameOffset = frame * bytesPerFrame;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frameOffset + (channel * bytesPerSample);
                sum += ReadSample(bytes, offset, format.BitsPerSample, isFloat);
            }

            mono[frame] = Math.Clamp((float)(sum / format.Channels), -1, 1);
        }

        return mono;
    }

    private static float ReadSample(
        ReadOnlySpan<byte> bytes,
        int offset,
        int bitsPerSample,
        bool isFloat)
    {
        if (isFloat && bitsPerSample == 32)
        {
            var value = BitConverter.ToSingle(bytes.Slice(offset, 4));
            return float.IsFinite(value) ? value : 0;
        }

        return bitsPerSample switch
        {
            16 => BitConverter.ToInt16(bytes.Slice(offset, 2)) / 32768f,
            24 => ReadInt24(bytes, offset) / 8_388_608f,
            32 => BitConverter.ToInt32(bytes.Slice(offset, 4)) / 2_147_483_648f,
            _ => throw new NotSupportedException(
                $"Unsupported PCM bit depth: {bitsPerSample}.")
        };
    }

    private static int ReadInt24(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = bytes[offset] |
                    (bytes[offset + 1] << 8) |
                    (bytes[offset + 2] << 16);
        return (value & 0x0080_0000) != 0
            ? value | unchecked((int)0xFF00_0000)
            : value;
    }
}
