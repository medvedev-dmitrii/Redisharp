using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisSharp.Core.Commands;
using RedisSharp.Core.Storage;
using RedisSharp.Server;
using Xunit;

namespace RedisSharp.Tests;

/// <summary>
/// Boots the real <see cref="RedisServer"/> on a random port and talks to it over raw TCP,
/// exactly like redis-cli would.
/// </summary>
public sealed class ServerIntegrationTests : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = GetFreePort();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.Configure<ServerOptions>(o => { o.Host = "127.0.0.1"; o.Port = _port; });
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<KeyValueStore>();
        builder.Services.AddSingleton(new ServerInfo("test", DateTimeOffset.UtcNow));
        builder.Services.AddSingleton(sp => new CommandContext(
            sp.GetRequiredService<KeyValueStore>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<ServerInfo>()));
        builder.Services.AddSingleton(sp => CommandDispatcher.CreateDefault(sp.GetRequiredService<CommandContext>()));
        builder.Services.AddHostedService<RedisServer>();

        _host = builder.Build();
        await _host.StartAsync();
        await WaitForPortAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Ping_over_tcp()
    {
        using var client = await ConnectAsync();
        var reply = await client.SendAsync("*1\r\n$4\r\nPING\r\n");
        reply.Should().Be("+PONG\r\n");
    }

    [Fact]
    public async Task Set_and_get_over_tcp()
    {
        using var client = await ConnectAsync();
        (await client.SendAsync("*3\r\n$3\r\nSET\r\n$3\r\nfoo\r\n$3\r\nbar\r\n")).Should().Be("+OK\r\n");
        (await client.SendAsync("*2\r\n$3\r\nGET\r\n$3\r\nfoo\r\n")).Should().Be("$3\r\nbar\r\n");
    }

    [Fact]
    public async Task Pipelined_commands_get_replies_in_order()
    {
        using var client = await ConnectAsync();
        var pipeline = "*3\r\n$3\r\nSET\r\n$1\r\nn\r\n$1\r\n0\r\n"
                     + "*2\r\n$4\r\nINCR\r\n$1\r\nn\r\n"
                     + "*2\r\n$4\r\nINCR\r\n$1\r\nn\r\n"
                     + "*2\r\n$3\r\nGET\r\n$1\r\nn\r\n";

        var reply = await client.SendAsync(pipeline, expectedReplies: 4);
        reply.Should().Be("+OK\r\n:1\r\n:2\r\n$1\r\n2\r\n");
    }

    [Fact]
    public async Task Inline_protocol_works_for_telnet_style_clients()
    {
        using var client = await ConnectAsync();
        (await client.SendAsync("SET a 1\r\n")).Should().Be("+OK\r\n");
        (await client.SendAsync("GET a\r\n")).Should().Be("$1\r\n1\r\n");
    }

    [Fact]
    public async Task Frame_split_across_writes_is_reassembled()
    {
        using var client = await ConnectAsync();
        await client.WriteAsync("*2\r\n$3\r\nGE");
        await Task.Delay(50);
        var reply = await client.SendAsync("T\r\n$3\r\nxyz\r\n");
        reply.Should().Be("$-1\r\n");
    }

    [Fact]
    public async Task Multiple_clients_share_the_same_store()
    {
        using var a = await ConnectAsync();
        using var b = await ConnectAsync();
        await a.SendAsync("SET shared 42\r\n");
        (await b.SendAsync("GET shared\r\n")).Should().Be("$2\r\n42\r\n");
    }

    [Fact]
    public async Task Unknown_command_does_not_kill_the_connection()
    {
        using var client = await ConnectAsync();
        (await client.SendAsync("NOPE\r\n")).Should().StartWith("-ERR unknown command");
        (await client.SendAsync("PING\r\n")).Should().Be("+PONG\r\n");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private async Task<RawClient> ConnectAsync()
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        return new RawClient(tcp);
    }

    private async Task WaitForPortAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, _port);
                return;
            }
            catch (SocketException) { await Task.Delay(20); }
        }
        throw new TimeoutException("Server did not start listening");
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private sealed class RawClient(TcpClient tcp) : IDisposable
    {
        private readonly NetworkStream _stream = tcp.GetStream();

        public Task WriteAsync(string raw) =>
            _stream.WriteAsync(Encoding.UTF8.GetBytes(raw)).AsTask();

        /// <summary>Writes a request and reads until <paramref name="expectedReplies"/> complete RESP replies have arrived.</summary>
        public async Task<string> SendAsync(string raw, int expectedReplies = 1)
        {
            await WriteAsync(raw);
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (CountReplies(sb.ToString()) < expectedReplies)
            {
                var n = await _stream.ReadAsync(buffer, cts.Token);
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }
            return sb.ToString();
        }

        private static int CountReplies(string s)
        {
            var seq = new System.Buffers.ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(s));
            var count = 0;
            while (Core.Protocol.RespParser.TryParse(seq, out _, out var consumed))
            {
                count++;
                seq = seq.Slice(consumed);
            }
            return count;
        }

        public void Dispose() => tcp.Dispose();
    }
}
