using System;

namespace Rangbaazi.Social.Chat;

/// <summary>
/// Token bucket for outgoing messages (anti-spam). Default: burst of 5, then 1 message every 2 s.
/// The server enforces its own limit too; this just gives the player immediate feedback.
/// </summary>
public sealed class ChatRateLimiter
{
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly TimeSpan _refillEvery;
    private double _tokens;
    private long _lastTimestamp;

    public ChatRateLimiter(TimeProvider time, int capacity = 5, TimeSpan? refillEvery = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _time = time;
        _capacity = capacity;
        _refillEvery = refillEvery ?? TimeSpan.FromSeconds(2);
        _tokens = capacity;
        _lastTimestamp = time.GetTimestamp();
    }

    public bool TryConsume()
    {
        long now = _time.GetTimestamp();
        TimeSpan elapsed = _time.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;
        _tokens = Math.Min(_capacity, _tokens + elapsed / _refillEvery);

        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}
