namespace RedisSharp.Core.Protocol;

/// <summary>
/// A value in the RESP2 wire protocol. Modelled as a closed hierarchy so command
/// handlers can pattern-match on the exact kind of value they received.
/// </summary>
public abstract record RespValue
{
    public static readonly RespValue Null = new RespNull();
    public static readonly RespValue Ok = new RespSimpleString("OK");
    public static readonly RespValue Pong = new RespSimpleString("PONG");

    public static RespValue Bulk(string value) => new RespBulkString(value);
    public static RespValue Integer(long value) => new RespInteger(value);
    public static RespValue Error(string message) => new RespError(message);
    public static RespValue Array(params RespValue[] items) => new RespArray(items);
    public static RespValue Array(IEnumerable<RespValue> items) => new RespArray(items.ToArray());

    /// <summary>Convenience for commands that need the argument as text.</summary>
    public string? AsString() => this switch
    {
        RespBulkString b => b.Value,
        RespSimpleString s => s.Value,
        RespInteger i => i.Value.ToString(),
        _ => null,
    };
}

/// <summary>Simple strings: <c>+OK\r\n</c>. Cannot contain CR or LF.</summary>
public sealed record RespSimpleString(string Value) : RespValue;

/// <summary>Errors: <c>-ERR unknown command\r\n</c>.</summary>
public sealed record RespError(string Message) : RespValue;

/// <summary>Integers: <c>:42\r\n</c>.</summary>
public sealed record RespInteger(long Value) : RespValue;

/// <summary>Bulk strings: <c>$5\r\nhello\r\n</c>. Binary-safe.</summary>
public sealed record RespBulkString(string Value) : RespValue;

/// <summary>Arrays: <c>*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n</c>.</summary>
public sealed record RespArray(IReadOnlyList<RespValue> Items) : RespValue
{
    public RespArray(params RespValue[] items) : this((IReadOnlyList<RespValue>)items) { }

    public bool Equals(RespArray? other) =>
        other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() =>
        Items.Aggregate(17, (h, v) => unchecked(h * 31 + v.GetHashCode()));
}

/// <summary>The null bulk string <c>$-1\r\n</c>.</summary>
public sealed record RespNull : RespValue;
