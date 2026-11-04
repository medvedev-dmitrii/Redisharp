using System.Buffers;
using System.Text;

namespace RedisSharp.Core.Protocol;

/// <summary>
/// Incremental, allocation-conscious RESP2 parser working over <see cref="ReadOnlySequence{T}"/>
/// so it plugs straight into <c>System.IO.Pipelines</c>. Returns <c>false</c> (without
/// consuming anything) when the buffer does not yet contain a complete value.
/// </summary>
public static class RespParser
{
    private const byte Cr = (byte)'\r';
    private const byte Lf = (byte)'\n';

    /// <summary>
    /// Tries to parse one complete RESP value from the start of <paramref name="buffer"/>.
    /// On success <paramref name="consumed"/> points just past the parsed value.
    /// </summary>
    /// <exception cref="RespProtocolException">Thrown on malformed input.</exception>
    public static bool TryParse(ReadOnlySequence<byte> buffer, out RespValue? value, out SequencePosition consumed)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (TryReadValue(ref reader, out value))
        {
            consumed = reader.Position;
            return true;
        }

        value = null;
        consumed = buffer.Start;
        return false;
    }

    private static bool TryReadValue(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        if (!reader.TryPeek(out byte prefix))
            return false;

        return prefix switch
        {
            (byte)'+' => TryReadSimple(ref reader, out value),
            (byte)'-' => TryReadError(ref reader, out value),
            (byte)':' => TryReadInteger(ref reader, out value),
            (byte)'$' => TryReadBulk(ref reader, out value),
            (byte)'*' => TryReadArray(ref reader, out value),
            // Inline commands ("PING\r\n") – handy when poking the server with netcat/telnet.
            _ => TryReadInline(ref reader, out value),
        };
    }

    private static bool TryReadLine(ref SequenceReader<byte> reader, out ReadOnlySequence<byte> line)
    {
        if (!reader.TryReadTo(out line, Cr, advancePastDelimiter: true))
            return false;

        if (!reader.TryRead(out byte lf))
        {
            // Have the CR but not the LF yet; rewind and wait for more data.
            reader.Rewind(line.Length + 1);
            return false;
        }

        if (lf != Lf)
            throw new RespProtocolException("Expected LF after CR");

        return true;
    }

    private static string Decode(in ReadOnlySequence<byte> seq) =>
        seq.IsSingleSegment ? Encoding.UTF8.GetString(seq.FirstSpan) : Encoding.UTF8.GetString(seq.ToArray());

    private static bool TryReadSimple(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        reader.Advance(1);
        if (!TryReadLine(ref reader, out var line)) { reader.Rewind(1); return false; }
        value = new RespSimpleString(Decode(line));
        return true;
    }

    private static bool TryReadError(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        reader.Advance(1);
        if (!TryReadLine(ref reader, out var line)) { reader.Rewind(1); return false; }
        value = new RespError(Decode(line));
        return true;
    }

    private static bool TryReadInteger(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        reader.Advance(1);
        if (!TryReadLine(ref reader, out var line)) { reader.Rewind(1); return false; }
        if (!long.TryParse(Decode(line), out var n))
            throw new RespProtocolException("Invalid integer");
        value = new RespInteger(n);
        return true;
    }

    private static bool TryReadBulk(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        var start = reader.Consumed;
        reader.Advance(1);
        if (!TryReadLine(ref reader, out var header)) { reader.Rewind(reader.Consumed - start); return false; }

        if (!int.TryParse(Decode(header), out var length))
            throw new RespProtocolException("Invalid bulk string length");

        if (length == -1)
        {
            value = RespValue.Null;
            return true;
        }

        if (length < 0 || length > 512 * 1024 * 1024)
            throw new RespProtocolException("Bulk string length out of range");

        if (reader.Remaining < length + 2)
        {
            reader.Rewind(reader.Consumed - start);
            return false;
        }

        var payload = reader.Sequence.Slice(reader.Position, length);
        reader.Advance(length);
        if (!reader.TryRead(out var cr) || cr != Cr || !reader.TryRead(out var lf) || lf != Lf)
            throw new RespProtocolException("Bulk string not terminated by CRLF");

        value = new RespBulkString(Decode(payload));
        return true;
    }

    private static bool TryReadArray(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        var start = reader.Consumed;
        reader.Advance(1);
        if (!TryReadLine(ref reader, out var header)) { reader.Rewind(reader.Consumed - start); return false; }

        if (!int.TryParse(Decode(header), out var count))
            throw new RespProtocolException("Invalid array length");

        if (count == -1)
        {
            value = RespValue.Null;
            return true;
        }

        if (count < 0 || count > 1024 * 1024)
            throw new RespProtocolException("Array length out of range");

        var items = new RespValue[count];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadValue(ref reader, out var item))
            {
                reader.Rewind(reader.Consumed - start);
                return false;
            }
            items[i] = item!;
        }

        value = new RespArray(items);
        return true;
    }

    private static bool TryReadInline(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        if (!TryReadLine(ref reader, out var line))
            return false;

        var parts = Decode(line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        value = new RespArray(parts.Select(p => (RespValue)new RespBulkString(p)).ToArray());
        return true;
    }
}

/// <summary>Raised when the byte stream violates the RESP grammar.</summary>
public sealed class RespProtocolException(string message) : Exception(message);
