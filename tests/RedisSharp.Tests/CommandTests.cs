using FluentAssertions;
using RedisSharp.Core.Commands;
using RedisSharp.Core.Protocol;
using RedisSharp.Core.Storage;
using Xunit;

namespace RedisSharp.Tests;

public class CommandTests
{
    private readonly FakeClock _clock = new();
    private readonly KeyValueStore _store;
    private readonly CommandDispatcher _dispatcher;

    public CommandTests()
    {
        _store = new KeyValueStore(_clock);
        var ctx = new CommandContext(_store, _clock, new ServerInfo("test", _clock.UtcNow));
        _dispatcher = CommandDispatcher.CreateDefault(ctx);
    }

    private RespValue Run(params string[] args) =>
        _dispatcher.Dispatch(RespValue.Array(args.Select(RespValue.Bulk)));

    [Fact]
    public void Ping_without_args_returns_pong()
    {
        Run("PING").Should().Be(RespValue.Pong);
    }

    [Fact]
    public void Ping_with_message_echoes_it_as_bulk()
    {
        Run("PING", "hi").Should().Be(RespValue.Bulk("hi"));
    }

    [Fact]
    public void Commands_are_case_insensitive()
    {
        Run("set", "k", "v").Should().Be(RespValue.Ok);
        Run("GeT", "k").Should().Be(RespValue.Bulk("v"));
    }

    [Fact]
    public void Unknown_command_returns_error()
    {
        Run("FROBNICATE", "x").Should().BeOfType<RespError>()
            .Which.Message.Should().StartWith("ERR unknown command 'FROBNICATE'");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("GET", "a", "b")]
    [InlineData("SET", "a")]
    [InlineData("INCR")]
    public void Wrong_arity_returns_error(params string[] args)
    {
        Run(args).Should().BeOfType<RespError>()
            .Which.Message.Should().Contain("wrong number of arguments");
    }

    [Fact]
    public void Get_missing_returns_null()
    {
        Run("GET", "nope").Should().Be(RespValue.Null);
    }

    [Fact]
    public void Set_with_EX_sets_ttl_in_seconds()
    {
        Run("SET", "k", "v", "EX", "10").Should().Be(RespValue.Ok);
        Run("TTL", "k").Should().Be(RespValue.Integer(10));
        _clock.Advance(TimeSpan.FromSeconds(10));
        Run("GET", "k").Should().Be(RespValue.Null);
    }

    [Fact]
    public void Set_with_PX_sets_ttl_in_milliseconds()
    {
        Run("SET", "k", "v", "PX", "1500").Should().Be(RespValue.Ok);
        Run("PTTL", "k").Should().Be(RespValue.Integer(1500));
        Run("TTL", "k").Should().Be(RespValue.Integer(2), "TTL rounds up like Redis");
    }

    [Fact]
    public void Set_NX_and_XX_behave_like_redis()
    {
        Run("SET", "k", "1", "NX").Should().Be(RespValue.Ok);
        Run("SET", "k", "2", "NX").Should().Be(RespValue.Null);
        Run("SET", "k", "3", "XX").Should().Be(RespValue.Ok);
        Run("SET", "other", "x", "XX").Should().Be(RespValue.Null);
        Run("GET", "k").Should().Be(RespValue.Bulk("3"));
    }

    [Theory]
    [InlineData("SET", "k", "v", "NX", "XX")]
    [InlineData("SET", "k", "v", "EX")]
    [InlineData("SET", "k", "v", "BOGUS")]
    public void Set_rejects_bad_options(params string[] args)
    {
        Run(args).Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR");
    }

    [Fact]
    public void Set_rejects_non_positive_ttl()
    {
        Run("SET", "k", "v", "EX", "0").Should().BeOfType<RespError>()
            .Which.Message.Should().Contain("invalid expire time");
    }

    [Fact]
    public void MGet_and_MSet()
    {
        Run("MSET", "a", "1", "b", "2").Should().Be(RespValue.Ok);
        Run("MGET", "a", "missing", "b").Should().Be(
            RespValue.Array(RespValue.Bulk("1"), RespValue.Null, RespValue.Bulk("2")));
    }

    [Fact]
    public void MSet_with_odd_args_is_an_error()
    {
        Run("MSET", "a", "1", "b").Should().BeOfType<RespError>();
    }

    [Fact]
    public void Incr_Decr_family()
    {
        Run("INCR", "n").Should().Be(RespValue.Integer(1));
        Run("INCRBY", "n", "9").Should().Be(RespValue.Integer(10));
        Run("DECR", "n").Should().Be(RespValue.Integer(9));
        Run("DECRBY", "n", "4").Should().Be(RespValue.Integer(5));
    }

    [Fact]
    public void Incr_on_string_value_returns_error()
    {
        Run("SET", "s", "abc");
        Run("INCR", "s").Should().BeOfType<RespError>()
            .Which.Message.Should().Be("ERR value is not an integer or out of range");
    }

    [Fact]
    public void Append_and_Strlen_count_bytes()
    {
        Run("APPEND", "s", "héllo").Should().Be(RespValue.Integer(6));
        Run("APPEND", "s", "!").Should().Be(RespValue.Integer(7));
        Run("STRLEN", "s").Should().Be(RespValue.Integer(7));
        Run("STRLEN", "missing").Should().Be(RespValue.Integer(0));
    }

    [Fact]
    public void Del_and_Exists_count_matches()
    {
        Run("MSET", "a", "1", "b", "2", "c", "3");
        Run("EXISTS", "a", "b", "zzz").Should().Be(RespValue.Integer(2));
        Run("DEL", "a", "b", "zzz").Should().Be(RespValue.Integer(2));
        Run("EXISTS", "a").Should().Be(RespValue.Integer(0));
    }

    [Fact]
    public void Keys_returns_sorted_matches()
    {
        Run("MSET", "user:2", "x", "user:1", "y", "order:1", "z");
        Run("KEYS", "user:*").Should().Be(RespValue.Array(RespValue.Bulk("user:1"), RespValue.Bulk("user:2")));
    }

    [Fact]
    public void Ttl_reports_minus_two_for_missing_and_minus_one_for_persistent()
    {
        Run("TTL", "missing").Should().Be(RespValue.Integer(-2));
        Run("SET", "k", "v");
        Run("TTL", "k").Should().Be(RespValue.Integer(-1));
    }

    [Fact]
    public void Expire_then_Persist()
    {
        Run("SET", "k", "v");
        Run("EXPIRE", "k", "60").Should().Be(RespValue.Integer(1));
        Run("TTL", "k").Should().Be(RespValue.Integer(60));
        Run("PERSIST", "k").Should().Be(RespValue.Integer(1));
        Run("TTL", "k").Should().Be(RespValue.Integer(-1));
        Run("EXPIRE", "missing", "60").Should().Be(RespValue.Integer(0));
    }

    [Fact]
    public void GetSet_returns_previous_value()
    {
        Run("GETSET", "k", "1").Should().Be(RespValue.Null);
        Run("GETSET", "k", "2").Should().Be(RespValue.Bulk("1"));
    }

    [Fact]
    public void FlushDb_and_DbSize()
    {
        Run("MSET", "a", "1", "b", "2");
        Run("DBSIZE").Should().Be(RespValue.Integer(2));
        Run("FLUSHDB").Should().Be(RespValue.Ok);
        Run("DBSIZE").Should().Be(RespValue.Integer(0));
    }

    [Fact]
    public void Info_contains_server_and_keyspace_sections()
    {
        Run("SET", "k", "v");
        var info = Run("INFO").Should().BeOfType<RespBulkString>().Which.Value;
        info.Should().Contain("# Server").And.Contain("redissharp_version:test").And.Contain("db0:keys=1");
    }

    [Fact]
    public void Non_array_request_is_a_protocol_error()
    {
        _dispatcher.Dispatch(RespValue.Bulk("GET")).Should().BeOfType<RespError>()
            .Which.Message.Should().Contain("protocol error");
    }
}
