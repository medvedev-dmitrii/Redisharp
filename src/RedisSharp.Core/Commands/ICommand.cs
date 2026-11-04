using RedisSharp.Core.Protocol;

namespace RedisSharp.Core.Commands;

/// <summary>A single server command such as GET or SET.</summary>
public interface ICommand
{
    /// <summary>Upper-case command name, e.g. <c>"GET"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Arity in Redis convention: positive = exact argument count (including the command name),
    /// negative = minimum argument count. <c>-2</c> means "at least one argument".
    /// </summary>
    int Arity { get; }

    RespValue Execute(CommandContext context, IReadOnlyList<string> args);
}

/// <summary>Everything a command needs to do its work.</summary>
public sealed record CommandContext(Storage.KeyValueStore Store, Storage.IClock Clock, ServerInfo Server);

/// <summary>Static facts about the running server, exposed via INFO.</summary>
public sealed record ServerInfo(string Version, DateTimeOffset StartedAt);
