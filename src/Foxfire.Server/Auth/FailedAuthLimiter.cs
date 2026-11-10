using System.Collections.Concurrent;

namespace Foxfire.Server.Auth;

/// <summary>
/// Fixed-window counter of failed credential attempts per client IP. Once an IP exceeds the limit,
/// credentialed endpoints answer 429 until the window rolls over.
/// </summary>
public sealed class FailedAuthLimiter(TimeProvider time)
{
    public int Limit { get; init; } = 20;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    private sealed class Counter
    {
        public DateTimeOffset WindowStart;
        public int Count;
    }

    public void RecordFailure(HttpContext context)
    {
        var now = time.GetUtcNow();
        var counter = _counters.GetOrAdd(Key(context), _ => new Counter { WindowStart = now });
        lock (counter)
        {
            if (now - counter.WindowStart >= Window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            counter.Count++;
        }

        if (_counters.Count > 10_000)
        {
            Prune(now);
        }
    }

    public bool IsBlocked(HttpContext context)
    {
        if (!_counters.TryGetValue(Key(context), out var counter))
        {
            return false;
        }

        lock (counter)
        {
            return time.GetUtcNow() - counter.WindowStart < Window && counter.Count >= Limit;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, counter) in _counters)
        {
            if (now - counter.WindowStart >= Window)
            {
                _counters.TryRemove(key, out _);
            }
        }
    }

    private static string Key(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
