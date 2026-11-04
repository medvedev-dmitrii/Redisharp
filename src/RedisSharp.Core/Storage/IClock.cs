namespace RedisSharp.Core.Storage;

/// <summary>Abstracts time so expiry logic can be tested deterministically.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
