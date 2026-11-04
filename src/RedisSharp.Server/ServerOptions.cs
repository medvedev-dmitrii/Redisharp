namespace RedisSharp.Server;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6379;

    /// <summary>How often the background sweeper removes expired keys.</summary>
    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromSeconds(1);
}
