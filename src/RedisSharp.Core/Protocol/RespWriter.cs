using System.Buffers;
using System.Globalization;
using System.Text;

namespace RedisSharp.Core.Protocol;

/// <summary>Serialises <see cref="RespValue"/> trees into RESP2 bytes.</summary>
public static class RespWriter
{
    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    public static void Write(IBufferWriter<byte> output, RespValue value)
    {
        switch (value)
        {
            case RespSimpleString s:
                WriteByte(output, (byte)'+');
                WriteText(output, s.Value);
                WriteCrlf(output);
                break;

            case RespError e:
                WriteByte(output, (byte)'-');
                WriteText(output, e.Message);
                WriteCrlf(output);
                break;

            case RespInteger i:
                WriteByte(output, (byte)':');
                WriteText(output, i.Value.ToString(CultureInfo.InvariantCulture));
                WriteCrlf(output);
                break;

            case RespBulkString b:
                var byteCount = Encoding.UTF8.GetByteCount(b.Value);
                WriteByte(output, (byte)'$');
                WriteText(output, byteCount.ToString(CultureInfo.InvariantCulture));
                WriteCrlf(output);
                WriteText(output, b.Value);
                WriteCrlf(output);
                break;

            case RespNull:
                output.Write("$-1\r\n"u8);
                break;

            case RespArray a:
                WriteByte(output, (byte)'*');
                WriteText(output, a.Items.Count.ToString(CultureInfo.InvariantCulture));
                WriteCrlf(output);
                foreach (var item in a.Items)
                    Write(output, item);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown RESP value type");
        }
    }

    /// <summary>Serialises to a standalone byte array – mostly useful in tests.</summary>
    public static byte[] ToBytes(RespValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteByte(IBufferWriter<byte> output, byte b)
    {
        output.GetSpan(1)[0] = b;
        output.Advance(1);
    }

    private static void WriteText(IBufferWriter<byte> output, string text)
    {
        var max = Encoding.UTF8.GetMaxByteCount(text.Length);
        var span = output.GetSpan(max);
        var written = Encoding.UTF8.GetBytes(text, span);
        output.Advance(written);
    }

    private static void WriteCrlf(IBufferWriter<byte> output) => output.Write(Crlf);
}
