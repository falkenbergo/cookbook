namespace RhythmLume.Hue;

public sealed class HueUpdateRateLimiter
{
    private readonly TimeSpan _interval;
    private readonly object _sync = new();
    private DateTimeOffset _nextAvailable = DateTimeOffset.MinValue;

    public HueUpdateRateLimiter(int maximumUpdatesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUpdatesPerSecond, 1);
        _interval = TimeSpan.FromSeconds(1d / maximumUpdatesPerSecond);
    }

    public TimeSpan ReserveDelay(DateTimeOffset now)
    {
        lock (_sync)
        {
            var reserved = now > _nextAvailable ? now : _nextAvailable;
            _nextAvailable = reserved + _interval;
            return reserved - now;
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var delay = ReserveDelay(DateTimeOffset.UtcNow);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _nextAvailable = DateTimeOffset.MinValue;
        }
    }
}
