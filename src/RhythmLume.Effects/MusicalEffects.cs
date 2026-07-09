using RhythmLume.Core;

namespace RhythmLume.Effects;

public sealed class MusicalColorEngine : IColorEngine
{
    public RgbColor GetColor(
        AnalysisFrame frame,
        int lightIndex,
        int lightCount,
        EffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Monochrome || settings.Palette.Count == 0)
        {
            return RgbColor.White;
        }

        var palette = settings.Palette;
        var position = (
            (float)(frame.AudioTime.TotalSeconds * (0.025 + (frame.OverallEnergy * 0.08))) +
            (lightCount <= 1 ? 0 : lightIndex / (float)lightCount)) % 1f;
        var scaled = position * palette.Count;
        var first = (int)MathF.Floor(scaled) % palette.Count;
        var second = (first + 1) % palette.Count;
        var amount = scaled - MathF.Floor(scaled);
        var baseColor = RgbColor.Lerp(palette[first], palette[second], amount);

        var bassWeight = Math.Clamp(
            frame.NormalizedBandEnergy.SubBass +
            (frame.NormalizedBandEnergy.Bass * 0.5f),
            0,
            1);
        var treble = frame.NormalizedBandEnergy.Highs;
        var deepened = RgbColor.Lerp(baseColor, new RgbColor(0.08f, 0.015f, 0.35f), bassWeight * 0.28f);
        var accented = RgbColor.Lerp(deepened, RgbColor.White, treble * 0.14f);
        return accented.Clamp(settings.SaturationLimit);
    }
}

public sealed class MusicalLightEffectEngine : ILightEffectEngine
{
    private readonly IColorEngine _colorEngine;
    private readonly Dictionary<string, float> _levels = new(StringComparer.Ordinal);
    private DateTimeOffset? _previousTimestamp;
    private int _chaseIndex = -1;
    private int _bounceDirection = 1;
    private int _alternatingStep;
    private float _wavePhase;

    public MusicalLightEffectEngine(IColorEngine colorEngine)
    {
        _colorEngine = colorEngine;
    }

    public EffectMode Mode { get; set; } = EffectMode.BassChase;

    public int CurrentChaseIndex => _chaseIndex;

    public void Reset()
    {
        _levels.Clear();
        _previousTimestamp = null;
        _chaseIndex = -1;
        _bounceDirection = 1;
        _alternatingStep = 0;
        _wavePhase = 0;
    }

    public IReadOnlyList<LightUpdate> Render(
        AnalysisFrame frame,
        IReadOnlyList<HueLight> lights,
        EffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(settings);
        if (lights.Count == 0)
        {
            return [];
        }

        Mode = settings.Mode;
        var elapsed = _previousTimestamp is null
            ? TimeSpan.FromMilliseconds(16)
            : frame.Timestamp - _previousTimestamp.Value;
        elapsed = TimeSpan.FromMilliseconds(Math.Clamp(elapsed.TotalMilliseconds, 1, 250));
        _previousTimestamp = frame.Timestamp;

        var targets = new float[lights.Count];
        switch (Mode)
        {
            case EffectMode.BassChase:
                RenderBassChase(frame, lights.Count, settings, targets);
                break;
            case EffectMode.BassPulse:
                RenderBassPulse(frame, settings, targets);
                break;
            case EffectMode.FrequencySplit:
                RenderFrequencySplit(frame, settings, targets);
                break;
            case EffectMode.EnergyWave:
                RenderEnergyWave(frame, lights.Count, settings, elapsed, targets);
                break;
            case EffectMode.CalmAmbient:
                RenderCalmAmbient(frame, lights.Count, elapsed, targets);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.Mode, "Unsupported effect.");
        }

        var output = new LightUpdate[lights.Count];
        for (var index = 0; index < lights.Count; index++)
        {
            var light = lights[index];
            var target = Math.Clamp(targets[index], 0, 1);
            var previous = _levels.GetValueOrDefault(light.Id);
            var timeConstant = target > previous
                ? Math.Max(1, settings.AttackMs)
                : Math.Max(1, settings.ReleaseMs);
            if (settings.StrobeSafe && target > previous)
            {
                timeConstant = Math.Max(timeConstant, 45);
                target = Math.Min(target, previous + settings.MaximumFlashIntensity);
            }

            var alpha = 1f - MathF.Exp(-(float)elapsed.TotalMilliseconds / timeConstant);
            var level = previous + ((target - previous) * Math.Clamp(alpha, 0, 1));
            level = Math.Clamp(level, 0, Math.Min(settings.BrightnessLimit, 1));
            _levels[light.Id] = level;
            var transition = TimeSpan.FromMilliseconds(
                target > previous ? settings.AttackMs : settings.ReleaseMs);
            output[index] = new LightUpdate(
                    light.Id,
                    level,
                    _colorEngine.GetColor(frame, index, lights.Count, settings),
                    transition)
                .Clamp(settings.BrightnessLimit, settings.SaturationLimit);
        }

        RemoveStaleLights(lights);
        return output;
    }

    private void RenderBassChase(
        AnalysisFrame frame,
        int lightCount,
        EffectSettings settings,
        float[] targets)
    {
        for (var index = 0; index < targets.Length; index++)
        {
            targets[index] = frame.NormalizedBandEnergy.SubBass * 0.10f;
        }

        if (frame.BassHit is null)
        {
            return;
        }

        _chaseIndex = Advance(lightCount, settings.ChaseDirection);
        var strength = Math.Clamp(
            frame.BassHit.Strength * settings.BassSensitivity,
            0.18f,
            1);
        targets[_chaseIndex] = strength;
        if (strength < 0.72f || lightCount < 2)
        {
            return;
        }

        var neighborLevel = strength * 0.48f;
        targets[(_chaseIndex - 1 + lightCount) % lightCount] =
            Math.Max(targets[(_chaseIndex - 1 + lightCount) % lightCount], neighborLevel);
        targets[(_chaseIndex + 1) % lightCount] =
            Math.Max(targets[(_chaseIndex + 1) % lightCount], neighborLevel);
    }

    private static void RenderBassPulse(
        AnalysisFrame frame,
        EffectSettings settings,
        float[] targets)
    {
        var swell = frame.NormalizedBandEnergy.SubBass * 0.35f;
        var pulse = frame.BassHit is null
            ? 0
            : frame.BassHit.Strength * settings.BassSensitivity;
        var level = Math.Clamp(Math.Max(swell, pulse), 0, 1);
        Array.Fill(targets, level);
    }

    private static void RenderFrequencySplit(
        AnalysisFrame frame,
        EffectSettings settings,
        float[] targets)
    {
        for (var index = 0; index < targets.Length; index++)
        {
            var position = (index + 0.5f) / targets.Length;
            targets[index] = position switch
            {
                < 0.34f => Math.Clamp(
                    Math.Max(
                        frame.NormalizedBandEnergy.SubBass,
                        frame.NormalizedBandEnergy.Bass) * settings.BassSensitivity,
                    0,
                    1),
                < 0.67f => Math.Clamp(
                    Math.Max(
                        frame.NormalizedBandEnergy.LowMids,
                        frame.NormalizedBandEnergy.Mids) * settings.MidSensitivity,
                    0,
                    1),
                _ => Math.Clamp(
                    frame.NormalizedBandEnergy.Highs *
                    settings.TrebleSensitivity *
                    (settings.StrobeSafe ? 0.72f : 1),
                    0,
                    1)
            };
        }
    }

    private void RenderEnergyWave(
        AnalysisFrame frame,
        int lightCount,
        EffectSettings settings,
        TimeSpan elapsed,
        float[] targets)
    {
        _wavePhase += (float)elapsed.TotalSeconds *
                      (0.8f + (frame.OverallEnergy * 2.2f));
        if (frame.BassHit is not null)
        {
            _wavePhase += 0.30f + (frame.BassHit.Strength * 0.25f);
        }

        for (var index = 0; index < lightCount; index++)
        {
            var normalizedPosition = lightCount <= 1 ? 0 : index / (float)(lightCount - 1);
            var wave = 0.5f + (0.5f * MathF.Sin(
                (_wavePhase - (normalizedPosition * 1.35f)) * MathF.Tau));
            targets[index] = Math.Clamp(
                (frame.OverallEnergy * 0.42f) +
                (wave * frame.OverallEnergy * 0.58f) +
                ((frame.BassHit?.Strength ?? 0) * wave * settings.BassSensitivity * 0.25f),
                0,
                1);
        }
    }

    private void RenderCalmAmbient(
        AnalysisFrame frame,
        int lightCount,
        TimeSpan elapsed,
        float[] targets)
    {
        _wavePhase += (float)elapsed.TotalSeconds * 0.055f;
        for (var index = 0; index < lightCount; index++)
        {
            var phaseOffset = lightCount <= 1 ? 0 : index / (float)lightCount;
            var drift = 0.5f + (0.5f * MathF.Sin(
                (_wavePhase + phaseOffset) * MathF.Tau));
            targets[index] = Math.Clamp(
                0.08f + (frame.OverallEnergy * 0.36f) + (drift * 0.12f),
                0,
                0.62f);
        }
    }

    private int Advance(int lightCount, ChaseDirection direction)
    {
        if (lightCount == 1)
        {
            return 0;
        }

        switch (direction)
        {
            case ChaseDirection.Forward:
                return (_chaseIndex + 1 + lightCount) % lightCount;
            case ChaseDirection.Reverse:
                return _chaseIndex < 0
                    ? lightCount - 1
                    : (_chaseIndex - 1 + lightCount) % lightCount;
            case ChaseDirection.Bounce:
                if (_chaseIndex < 0)
                {
                    _bounceDirection = 1;
                    return 0;
                }

                var next = _chaseIndex + _bounceDirection;
                if (next >= lightCount)
                {
                    _bounceDirection = -1;
                    next = lightCount - 2;
                }
                else if (next < 0)
                {
                    _bounceDirection = 1;
                    next = 1;
                }

                return next;
            case ChaseDirection.Alternating:
                var alternating = _alternatingStep % 2 == 0
                    ? _alternatingStep / 2
                    : lightCount - 1 - (_alternatingStep / 2);
                _alternatingStep = (_alternatingStep + 1) % lightCount;
                return Math.Clamp(alternating, 0, lightCount - 1);
            case ChaseDirection.CenterOut:
                var center = (lightCount - 1) / 2f;
                var step = _alternatingStep++ % lightCount;
                var offset = (step + 1) / 2;
                var centered = step == 0
                    ? (int)MathF.Floor(center)
                    : step % 2 == 1
                        ? (int)MathF.Ceiling(center + offset)
                        : (int)MathF.Floor(center - offset);
                return Math.Clamp(centered, 0, lightCount - 1);
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }
    }

    private void RemoveStaleLights(IReadOnlyList<HueLight> lights)
    {
        var active = lights.Select(static light => light.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _levels.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _levels.Remove(id);
        }
    }
}

public sealed class BuiltInPresetStore : IPresetStore
{
    private static readonly RgbColor[] ClubPalette =
    [
        new(0.95f, 0.02f, 0.30f),
        new(0.18f, 0.02f, 0.95f),
        new(0.02f, 0.80f, 0.95f)
    ];

    private static readonly RgbColor[] LoungePalette =
    [
        new(0.18f, 0.04f, 0.45f),
        new(0.02f, 0.30f, 0.48f),
        new(0.75f, 0.18f, 0.08f)
    ];

    public IReadOnlyList<PresetDefinition> GetBuiltInPresets() =>
    [
        Create("Bass Chase", "Focused kick-driven movement through the room.",
            EffectMode.BassChase, 1.05f, 35, 320, 0.86f, ClubPalette, true, 8, 50),
        Create("Club Mode", "Fast all-room bass pulses with saturated club colors.",
            EffectMode.BassPulse, 1.30f, 25, 210, 1.00f, ClubPalette, false, 9, 55),
        Create("Smooth Lounge", "Soft frequency movement with long releases.",
            EffectMode.FrequencySplit, 0.72f, 120, 720, 0.62f, LoungePalette, true, 6, 40),
        Create("Deep Bass Room", "Sub-bass swells and a deliberate center-out chase.",
            EffectMode.BassChase, 1.45f, 45, 480, 0.90f, LoungePalette, true, 8, 50,
            ChaseDirection.CenterOut),
        Create("Spectrum Split", "Maps lows, mids, and treble across the selected order.",
            EffectMode.FrequencySplit, 1.00f, 55, 300, 0.82f, ClubPalette, true, 8, 50),
        Create("Ambient Flow", "Slow palette drift driven by average song energy.",
            EffectMode.CalmAmbient, 0.55f, 240, 1_100, 0.58f, LoungePalette, true, 5, 30),
        Create("High Energy", "Responsive traveling waves reinforced by every kick.",
            EffectMode.EnergyWave, 1.35f, 25, 190, 1.00f, ClubPalette, false, 10, 60),
        Create("Minimal Flicker", "Restrained brightness-only ambience with maximum smoothing.",
            EffectMode.CalmAmbient, 0.62f, 180, 1_250, 0.48f, [RgbColor.White], true, 4, 30,
            monochrome: true)
    ];

    private static PresetDefinition Create(
        string name,
        string description,
        EffectMode mode,
        float bassSensitivity,
        int attack,
        int release,
        float brightness,
        IEnumerable<RgbColor> palette,
        bool strobeSafe,
        int compatibilityRate,
        int entertainmentRate,
        ChaseDirection direction = ChaseDirection.Forward,
        bool monochrome = false)
    {
        var settings = new EffectSettings
        {
            Mode = mode,
            BassSensitivity = bassSensitivity,
            AttackMs = attack,
            ReleaseMs = release,
            BrightnessLimit = brightness,
            Palette = palette.ToList(),
            StrobeSafe = strobeSafe,
            MaximumFlashIntensity = strobeSafe ? 0.58f : 0.92f,
            ChaseDirection = direction,
            Monochrome = monochrome
        };
        return new(
            name,
            description,
            settings,
            compatibilityRate,
            entertainmentRate);
    }
}
