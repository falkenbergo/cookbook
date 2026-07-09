using System.Numerics;
using RhythmLume.Core;
using RhythmLume.Dsp;
using RhythmLume.Effects;
using RhythmLume.Hue;

namespace RhythmLume.Tests;

public sealed class SignalAnalysisTests
{
    [Fact]
    public void Rms_UsesEverySample()
    {
        var rms = SignalMath.CalculateRms([1f, -1f, 1f, -1f]);
        Assert.Equal(1f, rms, 5);
    }

    [Fact]
    public void FftBandMapping_PlacesBinCenteredBassToneInBassBand()
    {
        const int sampleRate = 48_000;
        const int fftSize = 2_048;
        const float frequency = 93.75f;
        var transform = new Complex[fftSize];
        for (var index = 0; index < fftSize; index++)
        {
            var sample = Math.Sin(2 * Math.PI * frequency * index / sampleRate);
            var window = 0.5 - (0.5 * Math.Cos(2 * Math.PI * index / fftSize));
            transform[index] = new(sample * window, 0);
        }

        Radix2Fft.Forward(transform);
        var power = new float[(fftSize / 2) + 1];
        for (var bin = 0; bin < power.Length; bin++)
        {
            var magnitude = transform[bin].Magnitude * (4d / fftSize);
            power[bin] = (float)(magnitude * magnitude);
        }

        var bands = FrequencyBandMapper.Calculate(power, sampleRate, fftSize);
        Assert.True(bands.Bass > bands.SubBass);
        Assert.True(bands.Bass > bands.LowMids);
        Assert.True(bands.Bass > 0.3f);
    }

    [Fact]
    public void EnergySmoother_UsesFasterAttackThanRelease()
    {
        var smoother = new EnergySmoother();
        var attacked = smoother.Update(1, TimeSpan.FromMilliseconds(25), 25, 200);
        var released = smoother.Update(0, TimeSpan.FromMilliseconds(25), 25, 200);

        Assert.InRange(attacked, 0.60f, 0.66f);
        Assert.True(released > attacked * 0.8f);
    }

    [Fact]
    public void BassDetector_AdaptsAndHonorsCooldown()
    {
        var detector = new AdaptiveBassDetector(historyLength: 16, cooldownMs: 120, noiseFloor: 0.01f);
        var timestamp = DateTimeOffset.UtcNow;
        for (var index = 0; index < 16; index++)
        {
            Assert.Null(detector.Process(
                timestamp.AddMilliseconds(index * 20),
                0.10f,
                0.002f,
                1));
        }

        var first = detector.Process(timestamp.AddMilliseconds(400), 0.78f, 0.68f, 1);
        var suppressed = detector.Process(timestamp.AddMilliseconds(455), 0.92f, 0.75f, 1);
        var second = detector.Process(timestamp.AddMilliseconds(560), 0.96f, 0.80f, 1);

        Assert.NotNull(first);
        Assert.Null(suppressed);
        Assert.NotNull(second);
        Assert.InRange(first!.Strength, 0, 1);
        Assert.InRange(first.Confidence, 0, 1);
    }
}

public sealed class EffectEngineTests
{
    private static readonly HueLight[] Lights =
    [
        new("one", "1", "One", "test", true, true, Order: 0),
        new("two", "2", "Two", "test", true, true, Order: 1),
        new("three", "3", "Three", "test", true, true, Order: 2)
    ];

    [Fact]
    public void BassChase_AdvancesExactlyOneLightPerHit()
    {
        var engine = new MusicalLightEffectEngine(new MusicalColorEngine());
        var settings = new EffectSettings
        {
            Mode = EffectMode.BassChase,
            AttackMs = 1,
            ReleaseMs = 100,
            StrobeSafe = false,
            BrightnessLimit = 1
        };
        var started = DateTimeOffset.UtcNow;

        var first = engine.Render(CreateFrame(started, 0.8f), Lights, settings);
        var second = engine.Render(CreateFrame(started.AddMilliseconds(150), 0.8f), Lights, settings);

        Assert.Equal(1, engine.CurrentChaseIndex);
        Assert.Equal("one", first.MaxBy(static update => update.Brightness)!.LightId);
        Assert.Equal("two", second.MaxBy(static update => update.Brightness)!.LightId);
    }

    [Fact]
    public void EveryEffect_ClampsBrightnessToUserLimit()
    {
        var engine = new MusicalLightEffectEngine(new MusicalColorEngine());
        foreach (var mode in Enum.GetValues<EffectMode>())
        {
            engine.Reset();
            var settings = new EffectSettings
            {
                Mode = mode,
                AttackMs = 1,
                ReleaseMs = 1,
                StrobeSafe = false,
                BrightnessLimit = 0.37f
            };
            var output = engine.Render(CreateFrame(DateTimeOffset.UtcNow, 1), Lights, settings);
            Assert.All(output, update => Assert.InRange(update.Brightness, 0, 0.37f));
        }
    }

    [Fact]
    public void BuiltInStore_ContainsAllEightDocumentedPresets()
    {
        var names = new BuiltInPresetStore().GetBuiltInPresets()
            .Select(static preset => preset.Name)
            .ToArray();
        Assert.Equal(
            [
                "Bass Chase",
                "Club Mode",
                "Smooth Lounge",
                "Deep Bass Room",
                "Spectrum Split",
                "Ambient Flow",
                "High Energy",
                "Minimal Flicker"
            ],
            names);
    }

    private static AnalysisFrame CreateFrame(DateTimeOffset timestamp, float strength) => new(
        timestamp,
        TimeSpan.Zero,
        0.8f,
        new(0.5f, 0.8f, 0.3f, 0.4f, 0.2f),
        new(0.5f, 0.8f, 0.3f, 0.4f, 0.2f),
        0.7f,
        0.4f,
        new(timestamp, strength, 0.9f),
        new float[96]);
}

public sealed class HueOutputTests
{
    [Fact]
    public void EntertainmentPacket_UsesV2HeaderAndBigEndianRgb()
    {
        const string configurationId = "6eaf3b98-418d-48f3-89e4-a374cf9ef290";
        var packet = HueStreamPacketEncoder.Encode(
            configurationId,
            7,
            new Dictionary<byte, RgbColor>
            {
                [3] = new(1, 0.5f, 0)
            });

        Assert.Equal(59, packet.Length);
        Assert.Equal("HueStream", System.Text.Encoding.ASCII.GetString(packet, 0, 9));
        Assert.Equal(2, packet[9]);
        Assert.Equal(7, packet[11]);
        Assert.Equal(configurationId, System.Text.Encoding.ASCII.GetString(packet, 16, 36));
        Assert.Equal(3, packet[52]);
        Assert.Equal(0xFF, packet[53]);
        Assert.Equal(0xFF, packet[54]);
        Assert.InRange(packet[55], 0x7F, 0x80);
        Assert.Equal(0, packet[57]);
        Assert.Equal(0, packet[58]);
    }

    [Fact]
    public void CompatibilityLimiter_ReservesNoMoreThanTenUpdatesPerSecond()
    {
        var limiter = new HueUpdateRateLimiter(10);
        var now = DateTimeOffset.UtcNow;
        var delays = Enumerable.Range(0, 10)
            .Select(_ => limiter.ReserveDelay(now))
            .ToArray();

        Assert.Equal(TimeSpan.Zero, delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delays[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(900), delays[9]);
    }

    [Fact]
    public void LightUpdateClamp_EnforcesBrightnessAndColorLimits()
    {
        var update = new LightUpdate(
            "one",
            2,
            new(2, -1, 0.5f),
            TimeSpan.FromMilliseconds(-10));
        var clamped = update.Clamp(0.6f, 1);

        Assert.Equal(0.6f, clamped.Brightness);
        Assert.InRange(clamped.Color.Red, 0, 1);
        Assert.InRange(clamped.Color.Green, 0, 1);
        Assert.Equal(TimeSpan.Zero, clamped.Transition);
    }
}
