using System.Buffers;
using System.Text;
using FluentAssertions;
using RedisSharp.Core.Protocol;
using Xunit;

namespace RedisSharp.Tests;

public class RespParserTests
{
    private static bool Parse(string raw, out RespValue? value)
    {
        var seq = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(raw));
        return RespParser.TryParse(seq, out value, out _);
    }

    [Fact]
    public void Parses_simple_string()
    {
        Parse("+OK\r\n", out var v).Should().BeTrue();
        v.Should().Be(new RespSimpleString("OK"));
    }

    [Fact]
    public void Parses_error()
    {
        Parse("-ERR boom\r\n", out var v).Should().BeTrue();
        v.Should().Be(new RespError("ERR boom"));
    }

    [Theory]
    [InlineData(":0\r\n", 0)]
    [InlineData(":42\r\n", 42)]
    [InlineData(":-7\r\n", -7)]
    public void Parses_integers(string raw, long expected)
    {
        Parse(raw, out var v).Should().BeTrue();
        v.Should().Be(new RespInteger(expected));
    }

    [Fact]
    public void Parses_bulk_string()
    {
        Parse("$5\r\nhello\r\n", out var v).Should().BeTrue();
        v.Should().Be(new RespBulkString("hello"));
    }

    [Fact]
    public void Parses_empty_bulk_string()
    {
        Parse("$0\r\n\r\n", out var v).Should().BeTrue();
        v.Should().Be(new RespBulkString(""));
    }

    [Fact]
    public void Parses_null_bulk_string()
    {
        Parse("$-1\r\n", out var v).Should().BeTrue();
        v.Should().Be(RespValue.Null);
    }

    [Fact]
    public void Parses_bulk_string_containing_crlf()
    {
        Parse("$7\r\na\r\nb\r\nc\r\n", out var v).Should().BeTrue();
        v.Should().Be(new RespBulkString("a\r\nb\r\nc"));
    }

    [Fact]
    public void Parses_array_of_bulk_strings()
    {
        Parse("*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n", out var v).Should().BeTrue();
        v.Should().Be(RespValue.Array(RespValue.Bulk("GET"), RespValue.Bulk("key")));
    }

    [Fact]
    public void Parses_nested_arrays()
    {
        Parse("*2\r\n*1\r\n:1\r\n*1\r\n+x\r\n", out var v).Should().BeTrue();
        v.Should().Be(RespValue.Array(
            RespValue.Array(RespValue.Integer(1)),
            RespValue.Array(new RespSimpleString("x"))));
    }

    [Fact]
    public void Parses_inline_command()
    {
        Parse("SET a b\r\n", out var v).Should().BeTrue();
        v.Should().Be(RespValue.Array(RespValue.Bulk("SET"), RespValue.Bulk("a"), RespValue.Bulk("b")));
    }

    [Theory]
    [InlineData("*2\r\n$3\r\nGET\r\n")]      // array missing second element
    [InlineData("$5\r\nhel")]               // bulk payload incomplete
    [InlineData("$5\r\nhello\r")]           // missing final LF
    [InlineData("+OK")]                     // no CRLF yet
    [InlineData("")]
    public void Returns_false_and_consumes_nothing_on_partial_input(string raw)
    {
        var seq = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(raw));
        RespParser.TryParse(seq, out var v, out var consumed).Should().BeFalse();
        v.Should().BeNull();
        consumed.Should().Be(seq.Start);
    }

    [Fact]
    public void Consumed_position_allows_draining_pipelined_frames()
    {
        var raw = "+A\r\n+B\r\n+C\r\n";
        var buffer = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(raw));
        var parsed = new List<RespValue>();

        while (RespParser.TryParse(buffer, out var v, out var consumed))
        {
            parsed.Add(v!);
            buffer = buffer.Slice(consumed);
        }

        parsed.Should().HaveCount(3);
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Handles_multi_segment_sequences()
    {
        // Split a frame across two segments to simulate a fragmented TCP read.
        var raw = Encoding.UTF8.GetBytes("*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n");
        var first = new Segment(raw.AsMemory(0, 9));
        var last = first.Append(raw.AsMemory(9));
        var seq = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);

        RespParser.TryParse(seq, out var v, out _).Should().BeTrue();
        v.Should().Be(RespValue.Array(RespValue.Bulk("GET"), RespValue.Bulk("key")));
    }

    [Theory]
    [InlineData(":abc\r\n")]
    [InlineData("$x\r\n")]
    [InlineData("$3\r\nabcXX")]
    public void Throws_on_malformed_input(string raw)
    {
        var act = () => Parse(raw, out _);
        act.Should().Throw<RespProtocolException>();
    }

    [Fact]
    public void Writer_and_parser_round_trip()
    {
        var original = RespValue.Array(
            new RespSimpleString("OK"),
            RespValue.Error("ERR x"),
            RespValue.Integer(-5),
            RespValue.Bulk("héllo\r\nwörld"),
            RespValue.Null,
            RespValue.Array());

        var bytes = RespWriter.ToBytes(original);
        RespParser.TryParse(new ReadOnlySequence<byte>(bytes), out var parsed, out _).Should().BeTrue();
        parsed.Should().Be(original);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
