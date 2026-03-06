using System.Collections.Concurrent;

namespace FlyerTracker.Api.Security;

/// <summary>
/// Simple in-memory rate limiter. Tracks request count per key (IP or name)
/// within a sliding window. Not shared across instances — sufficient for
/// single-instance dev / small-scale production.
/// </summary>
public class RateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindow> _windows = new();
    private readonly int _maxRequests;
    private readonly TimeSpan _window;

    public RateLimiter(int maxRequests, TimeSpan window)
    {
        _maxRequests = maxRequests;
        _window = window;
    }

    /// <summary>Returns true if the request is allowed; false if rate-limited.</summary>
    public bool IsAllowed(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var sw = _windows.GetOrAdd(key, _ => new SlidingWindow());

        lock (sw)
        {
            while (sw.Timestamps.Count > 0 && now - sw.Timestamps.Peek() > _window)
                sw.Timestamps.Dequeue();

            if (sw.Timestamps.Count >= _maxRequests)
                return false;

            sw.Timestamps.Enqueue(now);
            return true;
        }
    }

    public void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _windows)
        {
            lock (kvp.Value)
            {
                while (kvp.Value.Timestamps.Count > 0 && now - kvp.Value.Timestamps.Peek() > _window)
                    kvp.Value.Timestamps.Dequeue();

                if (kvp.Value.Timestamps.Count == 0)
                    _windows.TryRemove(kvp.Key, out _);
            }
        }
    }

    private class SlidingWindow
    {
        public Queue<DateTimeOffset> Timestamps { get; } = new();
    }
}

/// <summary>
/// Provides separate rate limiters for read and write operations.
/// Read:  120 requests / minute / IP  (dropdown loads, table views, map data)
/// Write:  15 requests / minute / IP  (save location, create/update/delete)
/// Auth:   10 requests / minute / IP  (login attempts)
/// </summary>
public class RateLimitProvider
{
    public RateLimiter Read { get; }
    public RateLimiter Write { get; }
    public RateLimiter Auth { get; }

    public RateLimitProvider(
        int readMax = 120, int writeMax = 15, int authMax = 10,
        int windowSeconds = 60)
    {
        var window = TimeSpan.FromSeconds(windowSeconds);
        Read = new RateLimiter(readMax, window);
        Write = new RateLimiter(writeMax, window);
        Auth = new RateLimiter(authMax, window);
    }
}
