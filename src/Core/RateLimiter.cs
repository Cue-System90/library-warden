using System.Diagnostics;

namespace LibraryWarden.Core;

/// <summary>Gemeinsame Bandbreitenbremse für alle laufenden Downloads (Token-Bucket).</summary>
public sealed class RateLimiter
{
    readonly object _sync = new();
    double _bytesPerSecond;
    double _available;
    long _lastTicks = Stopwatch.GetTimestamp();

    public void SetLimit(double bytesPerSecond)
    {
        lock (_sync)
        {
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
            _available = 0;
            _lastTicks = Stopwatch.GetTimestamp();
        }
    }

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_sync)
            {
                if (_bytesPerSecond <= 0) return;
                var now = Stopwatch.GetTimestamp();
                var refill = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds * _bytesPerSecond;
                _available = Math.Min(Math.Max(_bytesPerSecond, bytes), _available + refill);
                _lastTicks = now;
                if (_available >= bytes)
                {
                    _available -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds((bytes - _available) / _bytesPerSecond);
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wait, ct);
        }
    }
}
