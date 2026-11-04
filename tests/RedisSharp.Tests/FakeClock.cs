using RedisSharp.Core.Storage;

namespace RedisSharp.Tests;

/// <summary>Manually advanced clock so TTL tests never need Thread.Sleep.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}
