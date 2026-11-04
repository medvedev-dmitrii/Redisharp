using System.Globalization;
using System.Text;
using RedisSharp.Core.Protocol;

namespace RedisSharp.Core.Commands;

/// <summary>The command set shipped with the server. Each command is a tiny, independently testable class.</summary>
public static class BuiltInCommands
{
    public static IEnumerable<ICommand> All() =>
    [
        new PingCommand(), new EchoCommand(), new CommandCommand(), new InfoCommand(), new DbSizeCommand(),
        new GetCommand(), new SetCommand(), new SetNxCommand(), new GetSetCommand(), new MGetCommand(), new MSetCommand(),
        new DelCommand(), new ExistsCommand(), new KeysCommand(), new FlushDbCommand(),
        new IncrCommand(), new DecrCommand(), new IncrByCommand(), new DecrByCommand(),
        new AppendCommand(), new StrLenCommand(),
        new ExpireCommand(), new PExpireCommand(), new TtlCommand(), new PTtlCommand(), new PersistCommand(),
    ];
}

// ───────────────────────────── connection / server ─────────────────────────────

public sealed class PingCommand : ICommand
{
    public string Name => "PING";
    public int Arity => -1;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        args.Count > 1 ? RespValue.Bulk(args[1]) : RespValue.Pong;
}

public sealed class EchoCommand : ICommand
{
    public string Name => "ECHO";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) => RespValue.Bulk(args[1]);
}

/// <summary>redis-cli sends COMMAND on connect; answering with an empty array keeps it happy.</summary>
public sealed class CommandCommand : ICommand
{
    public string Name => "COMMAND";
    public int Arity => -1;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) => RespValue.Array();
}

public sealed class InfoCommand : ICommand
{
    public string Name => "INFO";
    public int Arity => -1;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args)
    {
        var uptime = ctx.Clock.UtcNow - ctx.Server.StartedAt;
        var sb = new StringBuilder()
            .AppendLine("# Server")
            .AppendLine("redis_version:7.0.0-redissharp")
            .AppendLine($"redissharp_version:{ctx.Server.Version}")
            .AppendLine($"os:{Environment.OSVersion}")
            .AppendLine($"dotnet_version:{Environment.Version}")
            .AppendLine($"uptime_in_seconds:{(long)uptime.TotalSeconds}")
            .AppendLine()
            .AppendLine("# Keyspace")
            .AppendLine($"db0:keys={ctx.Store.Count}");
        return RespValue.Bulk(sb.ToString());
    }
}

public sealed class DbSizeCommand : ICommand
{
    public string Name => "DBSIZE";
    public int Arity => 1;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) => RespValue.Integer(ctx.Store.Count);
}

// ───────────────────────────── strings ─────────────────────────────

public sealed class GetCommand : ICommand
{
    public string Name => "GET";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        ctx.Store.TryGet(args[1], out var v) ? RespValue.Bulk(v) : RespValue.Null;
}

/// <summary>SET key value [EX seconds | PX milliseconds] [NX | XX]</summary>
public sealed class SetCommand : ICommand
{
    public string Name => "SET";
    public int Arity => -3;

    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args)
    {
        TimeSpan? ttl = null;
        bool nx = false, xx = false;

        for (var i = 3; i < args.Count; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "EX":
                    ttl = TimeSpan.FromSeconds(ParseTtl(args, ++i));
                    break;
                case "PX":
                    ttl = TimeSpan.FromMilliseconds(ParseTtl(args, ++i));
                    break;
                case "NX": nx = true; break;
                case "XX": xx = true; break;
                default: throw new CommandException("ERR syntax error");
            }
        }

        if (nx && xx)
            throw new CommandException("ERR syntax error");

        if (nx)
            return ctx.Store.SetIfAbsent(args[1], args[2], ttl) ? RespValue.Ok : RespValue.Null;
        if (xx)
            return ctx.Store.SetIfPresent(args[1], args[2], ttl) ? RespValue.Ok : RespValue.Null;

        ctx.Store.Set(args[1], args[2], ttl);
        return RespValue.Ok;
    }

    private static long ParseTtl(IReadOnlyList<string> args, int index)
    {
        if (index >= args.Count)
            throw new CommandException("ERR syntax error");
        if (!long.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new CommandException("ERR invalid expire time in 'set' command");
        return n;
    }
}

public sealed class SetNxCommand : ICommand
{
    public string Name => "SETNX";
    public int Arity => 3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.SetIfAbsent(args[1], args[2]) ? 1 : 0);
}

public sealed class GetSetCommand : ICommand
{
    public string Name => "GETSET";
    public int Arity => 3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args)
    {
        var old = ctx.Store.TryGet(args[1], out var v) ? RespValue.Bulk(v) : RespValue.Null;
        ctx.Store.Set(args[1], args[2]);
        return old;
    }
}

public sealed class MGetCommand : ICommand
{
    public string Name => "MGET";
    public int Arity => -2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Array(args.Skip(1).Select(k => ctx.Store.TryGet(k, out var v) ? RespValue.Bulk(v) : RespValue.Null));
}

public sealed class MSetCommand : ICommand
{
    public string Name => "MSET";
    public int Arity => -3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args)
    {
        if ((args.Count - 1) % 2 != 0)
            throw new CommandException("ERR wrong number of arguments for 'mset' command");
        for (var i = 1; i < args.Count; i += 2)
            ctx.Store.Set(args[i], args[i + 1]);
        return RespValue.Ok;
    }
}

public sealed class AppendCommand : ICommand
{
    public string Name => "APPEND";
    public int Arity => 3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args)
    {
        var existing = ctx.Store.TryGet(args[1], out var v) ? v : string.Empty;
        var next = existing + args[2];
        ctx.Store.Set(args[1], next);
        return RespValue.Integer(Encoding.UTF8.GetByteCount(next));
    }
}

public sealed class StrLenCommand : ICommand
{
    public string Name => "STRLEN";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.TryGet(args[1], out var v) ? Encoding.UTF8.GetByteCount(v) : 0);
}

// ───────────────────────────── counters ─────────────────────────────

public sealed class IncrCommand : ICommand
{
    public string Name => "INCR";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.Increment(args[1], 1));
}

public sealed class DecrCommand : ICommand
{
    public string Name => "DECR";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.Increment(args[1], -1));
}

public sealed class IncrByCommand : ICommand
{
    public string Name => "INCRBY";
    public int Arity => 3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.Increment(args[1], Numbers.ParseLong(args[2])));
}

public sealed class DecrByCommand : ICommand
{
    public string Name => "DECRBY";
    public int Arity => 3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.Increment(args[1], checked(-Numbers.ParseLong(args[2]))));
}

// ───────────────────────────── keys ─────────────────────────────

public sealed class DelCommand : ICommand
{
    public string Name => "DEL";
    public int Arity => -2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(args.Skip(1).Count(ctx.Store.Delete));
}

public sealed class ExistsCommand : ICommand
{
    public string Name => "EXISTS";
    public int Arity => -2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(args.Skip(1).Count(ctx.Store.Exists));
}

public sealed class KeysCommand : ICommand
{
    public string Name => "KEYS";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Array(ctx.Store.Keys(args[1]).OrderBy(k => k, StringComparer.Ordinal).Select(RespValue.Bulk));
}

public sealed class FlushDbCommand : ICommand
{
    public string Name => "FLUSHDB";
    public int Arity => -1;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args)
    {
        ctx.Store.Clear();
        return RespValue.Ok;
    }
}

// ───────────────────────────── expiry ─────────────────────────────

public sealed class ExpireCommand : ICommand
{
    public string Name => "EXPIRE";
    public int Arity => 3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.Expire(args[1], TimeSpan.FromSeconds(Numbers.ParseLong(args[2]))) ? 1 : 0);
}

public sealed class PExpireCommand : ICommand
{
    public string Name => "PEXPIRE";
    public int Arity => 3;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.Expire(args[1], TimeSpan.FromMilliseconds(Numbers.ParseLong(args[2]))) ? 1 : 0);
}

public sealed class TtlCommand : ICommand
{
    public string Name => "TTL";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.GetTtl(args[1]) switch
        {
            null => -1,
            var t when t == TimeSpan.MinValue => -2,
            var t => (long)Math.Ceiling(t.Value.TotalSeconds),
        });
}

public sealed class PTtlCommand : ICommand
{
    public string Name => "PTTL";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.GetTtl(args[1]) switch
        {
            null => -1,
            var t when t == TimeSpan.MinValue => -2,
            var t => (long)Math.Ceiling(t.Value.TotalMilliseconds),
        });
}

public sealed class PersistCommand : ICommand
{
    public string Name => "PERSIST";
    public int Arity => 2;
    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Integer(ctx.Store.Persist(args[1]) ? 1 : 0);
}

internal static class Numbers
{
    public static long ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new CommandException("ERR value is not an integer or out of range");
}
