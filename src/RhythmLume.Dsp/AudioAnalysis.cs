using System.Numerics;
using System.Threading.Channels;
using RhythmLume.Core;

namespace RhythmLume.Dsp;

public static class SignalMath
{
    public static float CalculateRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            var finite = float.IsFinite(sample) ? sample : 0;
            sum += finite * finite;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    public static float LinearToDecibels(float value) =>
        value <= 0 ? -120 : Math.Max(-120, 20f * MathF.Log10(value));
}

public static class Radix2Fft
{
    public static void Forward(Span<Complex> values)
    {
        var length = values.Length;
        if (length < 2 || (length & (length - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two and at least two.", nameof(values));
        }

        for (var i = 1; i < length; i++)
        {
            var reversed = ReverseBits(i, BitOperations.Log2((uint)length));
            if (i < reversed)
            {
                (values[i], values[reversed]) = (values[reversed], values[i]);
            }
        }

        for (var size = 2; size <= length; size <<= 1)
        {
            var half = size / 2;
            var phaseStep = -2 * Math.PI / size;
            for (var start = 0; start < length; start += size)
            {
                for (var offset = 0; offset < half; offset++)
                {
                    var phase = phaseStep * offset;
                    var twiddle = new Complex(Math.Cos(phase), Math.Sin(phase));
                    var even = values[start + offset];
                    var odd = twiddle * values[start + offset + half];
                    values[start + offset] = even + odd;
                    values[start + offset + half] = even - odd;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bitCount)
    {
        var result = 0;
        for (var i = 0; i < bitCount; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}

public static class FrequencyBandMapper
{
    private static readonly (float Low, float High)[] Ranges =
    [
        (20, 60),
        (60, 150),
        (150, 400),
        (400, 2_000),
        (2_000, 10_000)
    ];

    public static BandValues Calculate(
        ReadOnlySpan<float> oneSidedPower,
        int sampleRate,
        int fftSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fftSize);
        if (oneSidedPower.IsEmpty)
        {
            return default;
        }

        Span<float> values = stackalloc float[Ranges.Length];
        for (var index = 0; index < Ranges.Length; index++)
        {
            values[index] = IntegrateBand(oneSidedPower, sampleRate, fftSize, Ranges[index]);
        }

        return new(values[0], values[1], values[2], values[3], values[4]);
    }

    private static float IntegrateBand(
        ReadOnlySpan<float> power,
        int sampleRate,
        int fftSize,
        (float Low, float High) range)
    {
        var binWidth = (float)sampleRate / fftSize;
        double weightedPower = 0;
        double totalWeight = 0;
        for (var bin = 0; bin < power.Length; bin++)
        {
            var center = bin * binWidth;
            var binLow = Math.Max(0, center - (binWidth / 2));
            var binHigh = center + (binWidth / 2);
            var overlap = Math.Max(0, Math.Min(binHigh, range.High) - Math.Max(binLow, range.Low));
            if (overlap <= 0)
            {
                continue;
            }

            var weight = overlap / binWidth;
            weightedPower += Math.Max(0, power[bin]) * weight;
            totalWeight += weight;
        }

        return totalWeight <= 0 ? 0 : (float)Math.Sqrt(weightedPower / totalWeight);
    }
}

public sealed class EnergySmoother
{
    private float _value;

    public float Update(float target, TimeSpan elapsed, int attackMs, int releaseMs)
    {
        target = Math.Clamp(target, 0, 1);
        var timeConstantMs = target > _value ? Math.Max(1, attackMs) : Math.Max(1, releaseMs);
        var alpha = 1f - MathF.Exp(-(float)elapsed.TotalMilliseconds / timeConstantMs);
        _value += (target - _value) * Math.Clamp(alpha, 0, 1);
        return _value;
    }

    public void Reset() => _value = 0;
}

public sealed class AdaptiveBassDetector : IBeatDetector
{
    private readonly Queue<float> _history;
    private readonly int _historyLength;
    private readonly int _cooldownMs;
    private readonly float _noiseFloor;
    private DateTimeOffset _lastHit = DateTimeOffset.MinValue;
    private float _previous;

    public AdaptiveBassDetector(
        int historyLength = 96,
        int cooldownMs = 120,
        float noiseFloor = 0.012f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(historyLength, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(cooldownMs, 1);
        _historyLength = historyLength;
        _cooldownMs = cooldownMs;
        _noiseFloor = Math.Max(0, noiseFloor);
        _history = new Queue<float>(historyLength);
    }

    public BeatEvent? Process(
        DateTimeOffset timestamp,
        float bassEnergy,
        float bassFlux,
        float sensitivity)
    {
        bassEnergy = Math.Max(0, bassEnergy);
        bassFlux = Math.Max(bassFlux, bassEnergy - _previous);
        _previous = bassEnergy;

        var median = Median(_history);
        var mad = MedianAbsoluteDeviation(_history, median);
        var adjustedSensitivity = Math.Clamp(sensitivity, 0.25f, 3f);
        var thresholdMargin = (0.055f + (1.8f * mad)) / adjustedSensitivity;
        var threshold = Math.Max(_noiseFloor, median + thresholdMargin);
        var elapsed = timestamp - _lastHit;
        var hasHistory = _history.Count >= 8;
        var isTransient = bassFlux > (0.018f / adjustedSensitivity);
        var isCandidate = hasHistory &&
                          bassEnergy > threshold &&
                          isTransient &&
                          elapsed.TotalMilliseconds >= _cooldownMs;

        Enqueue(bassEnergy);
        if (!isCandidate)
        {
            return null;
        }

        _lastHit = timestamp;
        var excess = (bassEnergy - threshold) / Math.Max(0.08f, 1 - threshold);
        var strength = Math.Clamp(0.25f + (excess * 1.5f), 0, 1);
        var confidence = Math.Clamp(
            0.45f + (excess * 0.35f) + (bassFlux * 1.5f),
            0,
            1);
        return new(timestamp, strength, confidence);
    }

    public void Reset()
    {
        _history.Clear();
        _lastHit = DateTimeOffset.MinValue;
        _previous = 0;
    }

    private void Enqueue(float value)
    {
        _history.Enqueue(value);
        while (_history.Count > _historyLength)
        {
            _history.Dequeue();
        }
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private static float MedianAbsoluteDeviation(IEnumerable<float> values, float median) =>
        Median(values.Select(value => Math.Abs(value - median)));
}

public sealed class RealTimeAudioAnalyzer : IAudioAnalyzer
{
    private const int SpectrumPointCount = 96;
    private readonly AnalysisOptions _options;
    private readonly IBeatDetector _beatDetector;
    private readonly Channel<AudioChunk> _chunks;
    private readonly EnergySmoother[] _smoothers =
        Enumerable.Range(0, 5).Select(_ => new EnergySmoother()).ToArray();
    private readonly AdaptiveNormalizer[] _normalizers =
        Enumerable.Range(0, 5).Select(_ => new AdaptiveNormalizer()).ToArray();
    private CancellationTokenSource? _runCancellation;
    private Task? _worker;
    private long _droppedChunks;

    public RealTimeAudioAnalyzer(AnalysisOptions options, IBeatDetector? beatDetector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
        _beatDetector = beatDetector ?? new AdaptiveBassDetector(
            cooldownMs: options.BeatCooldownMs,
            noiseFloor: options.NoiseFloor);
        _chunks = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(12)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public event Action<AnalysisFrame>? AnalysisAvailable;

    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    public bool TrySubmit(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_chunks.Writer.TryWrite(chunk))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedChunks);
        return false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null)
        {
            return Task.CompletedTask;
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_runCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_worker is null)
        {
            return;
        }

        await _runCancellation!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _worker = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await StopAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var state = new AnalysisState(_options.FftSize, _options.HopSize);
        try
        {
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.SampleRate <= 0 || chunk.Samples.Length == 0)
                {
                    continue;
                }

                if (state.SampleRate != chunk.SampleRate)
                {
                    Reset(state, chunk.SampleRate, chunk.CapturedAt);
                }

                ProcessChunk(state, chunk);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ProcessChunk(AnalysisState state, AudioChunk chunk)
    {
        foreach (var rawSample in chunk.Samples)
        {
            var sample = float.IsFinite(rawSample) ? Math.Clamp(rawSample, -4, 4) : 0;
            state.Ring[state.WriteIndex] = sample;
            state.WriteIndex = (state.WriteIndex + 1) % state.Ring.Length;
            state.TotalSamples++;
            state.SamplesSinceFrame++;

            var firstFrame = state.TotalSamples == state.Ring.Length;
            var nextFrame = state.TotalSamples > state.Ring.Length &&
                            state.SamplesSinceFrame >= state.HopSize;
            if (!firstFrame && !nextFrame)
            {
                continue;
            }

            state.SamplesSinceFrame = 0;
            EmitFrame(state);
        }
    }

    private void EmitFrame(AnalysisState state)
    {
        CopyChronological(state.Ring, state.WriteIndex, state.Frame);
        var inputLevel = Math.Clamp(SignalMath.CalculateRms(state.Frame), 0, 1);
        for (var index = 0; index < state.Frame.Length; index++)
        {
            state.Transform[index] = new Complex(state.Frame[index] * state.Window[index], 0);
        }

        Radix2Fft.Forward(state.Transform);
        var scale = 2f / (state.Frame.Length * 0.5f);
        for (var bin = 0; bin < state.Power.Length; bin++)
        {
            var magnitude = (float)state.Transform[bin].Magnitude * scale;
            state.Power[bin] = magnitude * magnitude;
        }

        var raw = FrequencyBandMapper.Calculate(state.Power, state.SampleRate, state.Frame.Length);
        var elapsed = TimeSpan.FromSeconds((double)state.HopSize / state.SampleRate);
        Span<float> normalized = stackalloc float[5];
        var rawValues = new[] { raw.SubBass, raw.Bass, raw.LowMids, raw.Mids, raw.Highs };
        for (var index = 0; index < rawValues.Length; index++)
        {
            var target = _normalizers[index].Update(rawValues[index], elapsed);
            normalized[index] = _smoothers[index].Update(
                target,
                elapsed,
                _options.AttackMs,
                _options.ReleaseMs);
        }

        var normalizedBands = new BandValues(
            normalized[0],
            normalized[1],
            normalized[2],
            normalized[3],
            normalized[4]).Clamp01();
        var spectralFlux = CalculateSpectralFlux(state.Power, state.PreviousPower);
        var bassFlux = Math.Max(0, normalizedBands.Bass - state.PreviousBass);
        state.PreviousBass = normalizedBands.Bass;
        state.Power.CopyTo(state.PreviousPower, 0);

        var audioTime = TimeSpan.FromSeconds((double)state.TotalSamples / state.SampleRate);
        var centeredTime = TimeSpan.FromSeconds(
            Math.Max(0, state.TotalSamples - (state.Frame.Length / 2d)) / state.SampleRate);
        var timestamp = state.StreamStartedAt + centeredTime;
        var hit = _beatDetector.Process(
            timestamp,
            normalizedBands.Bass,
            bassFlux + (spectralFlux * 0.25f),
            _options.BassSensitivity);
        var overall = Math.Clamp(
            (normalizedBands.SubBass * 0.16f) +
            (normalizedBands.Bass * 0.28f) +
            (normalizedBands.LowMids * 0.20f) +
            (normalizedBands.Mids * 0.22f) +
            (normalizedBands.Highs * 0.14f),
            0,
            1);
        var spectrum = CreateSpectrum(state.Power, state.SampleRate, state.Frame.Length);

        AnalysisAvailable?.Invoke(new(
            timestamp,
            audioTime,
            inputLevel,
            raw,
            normalizedBands,
            overall,
            spectralFlux,
            hit,
            spectrum));
    }

    private void Reset(AnalysisState state, int sampleRate, DateTimeOffset startedAt)
    {
        state.Reset(sampleRate, startedAt);
        _beatDetector.Reset();
        foreach (var smoother in _smoothers)
        {
            smoother.Reset();
        }

        foreach (var normalizer in _normalizers)
        {
            normalizer.Reset();
        }
    }

    private static float CalculateSpectralFlux(float[] current, float[] previous)
    {
        double sum = 0;
        var count = Math.Min(current.Length, previous.Length);
        for (var index = 1; index < count; index++)
        {
            var currentLog = MathF.Log10(current[index] + 1e-10f);
            var previousLog = MathF.Log10(previous[index] + 1e-10f);
            sum += Math.Clamp(currentLog - previousLog, 0, 1.2f);
        }

        return count <= 1 ? 0 : Math.Clamp((float)(sum / (count - 1)), 0, 1);
    }

    private static float[] CreateSpectrum(float[] power, int sampleRate, int fftSize)
    {
        var result = new float[SpectrumPointCount];
        var binWidth = (float)sampleRate / fftSize;
        var maximumFrequency = Math.Min(10_000, sampleRate / 2f);
        for (var point = 0; point < result.Length; point++)
        {
            var lowT = point / (float)result.Length;
            var highT = (point + 1) / (float)result.Length;
            var lowFrequency = 20 * MathF.Pow(maximumFrequency / 20, lowT);
            var highFrequency = 20 * MathF.Pow(maximumFrequency / 20, highT);
            var lowBin = Math.Clamp((int)(lowFrequency / binWidth), 0, power.Length - 1);
            var highBin = Math.Clamp((int)Math.Ceiling(highFrequency / binWidth), lowBin + 1, power.Length);
            double sum = 0;
            for (var bin = lowBin; bin < highBin; bin++)
            {
                sum += power[bin];
            }

            var rms = Math.Sqrt(sum / (highBin - lowBin));
            var decibels = rms <= 0 ? -100 : 10 * Math.Log10(rms);
            result[point] = Math.Clamp((float)((decibels + 80) / 80), 0, 1);
        }

        return result;
    }

    private static void CopyChronological(float[] ring, int oldestIndex, float[] destination)
    {
        var firstLength = ring.Length - oldestIndex;
        Array.Copy(ring, oldestIndex, destination, 0, firstLength);
        Array.Copy(ring, 0, destination, firstLength, oldestIndex);
    }

    private static void ValidateOptions(AnalysisOptions options)
    {
        if (options.FftSize < 256 || (options.FftSize & (options.FftSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "FFT size must be a power of two and at least 256.");
        }

        if (options.HopSize <= 0 || options.HopSize > options.FftSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Hop size must be positive and no larger than the FFT size.");
        }
    }

    private sealed class AnalysisState
    {
        public AnalysisState(int fftSize, int hopSize)
        {
            HopSize = hopSize;
            Ring = new float[fftSize];
            Frame = new float[fftSize];
            Transform = new Complex[fftSize];
            Power = new float[(fftSize / 2) + 1];
            PreviousPower = new float[Power.Length];
            Window = Enumerable.Range(0, fftSize)
                .Select(index => 0.5f - (0.5f * MathF.Cos(2 * MathF.PI * index / fftSize)))
                .ToArray();
        }

        public int HopSize { get; }
        public float[] Ring { get; }
        public float[] Frame { get; }
        public Complex[] Transform { get; }
        public float[] Power { get; }
        public float[] PreviousPower { get; }
        public float[] Window { get; }
        public int SampleRate { get; private set; }
        public int WriteIndex { get; set; }
        public long TotalSamples { get; set; }
        public int SamplesSinceFrame { get; set; }
        public float PreviousBass { get; set; }
        public DateTimeOffset StreamStartedAt { get; private set; }

        public void Reset(int sampleRate, DateTimeOffset startedAt)
        {
            Array.Clear(Ring);
            Array.Clear(Frame);
            Array.Clear(Transform);
            Array.Clear(Power);
            Array.Clear(PreviousPower);
            SampleRate = sampleRate;
            WriteIndex = 0;
            TotalSamples = 0;
            SamplesSinceFrame = 0;
            PreviousBass = 0;
            StreamStartedAt = startedAt;
        }
    }

    private sealed class AdaptiveNormalizer
    {
        private float _noiseFloorDb = -82;
        private float _peakDb = -42;

        public float Update(float linearEnergy, TimeSpan elapsed)
        {
            var decibels = SignalMath.LinearToDecibels(linearEnergy);
            var seconds = Math.Max(0.001, elapsed.TotalSeconds);
            if (decibels < _noiseFloorDb)
            {
                _noiseFloorDb += (decibels - _noiseFloorDb) * (float)Math.Min(1, seconds * 2);
            }
            else
            {
                _noiseFloorDb = Math.Min(_noiseFloorDb + (float)(seconds * 0.5), decibels - 3);
            }

            _peakDb = decibels >= _peakDb
                ? decibels
                : Math.Max(_noiseFloorDb + 18, _peakDb - (float)(seconds * 6));
            var gate = _noiseFloorDb + 6;
            var range = Math.Max(18, _peakDb - gate);
            var normalized = Math.Clamp((decibels - gate) / range, 0, 1);
            return normalized * normalized * (3 - (2 * normalized));
        }

        public void Reset()
        {
            _noiseFloorDb = -82;
            _peakDb = -42;
        }
    }
}
