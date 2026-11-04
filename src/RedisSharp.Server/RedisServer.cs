using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisSharp.Core.Commands;
using RedisSharp.Core.Storage;

namespace RedisSharp.Server;

/// <summary>Accept loop. One <see cref="ClientConnection"/> task per socket.</summary>
public sealed class RedisServer : BackgroundService
{
    private readonly ServerOptions _options;
    private readonly CommandDispatcher _dispatcher;
    private readonly ILogger<RedisServer> _logger;

    public RedisServer(IOptions<ServerOptions> options, CommandDispatcher dispatcher, ILogger<RedisServer> logger)
    {
        _options = options.Value;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(_options.Host), _options.Port);
        using var listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(endpoint);
        listener.Listen(backlog: 512);

        _logger.LogInformation("RedisSharp listening on {Endpoint} ({Count} commands)", endpoint, _dispatcher.CommandNames.Count);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(stoppingToken);
                socket.NoDelay = true;
                _ = Task.Run(() => new ClientConnection(socket, _dispatcher, _logger).RunAsync(stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Periodically evicts expired keys so memory is reclaimed even for keys nobody reads again.</summary>
public sealed class ExpirySweeper : BackgroundService
{
    private readonly KeyValueStore _store;
    private readonly TimeSpan _interval;
    private readonly ILogger<ExpirySweeper> _logger;

    public ExpirySweeper(KeyValueStore store, IOptions<ServerOptions> options, ILogger<ExpirySweeper> logger)
    {
        _store = store;
        _interval = options.Value.ExpirySweepInterval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var removed = _store.EvictExpired();
                if (removed > 0)
                    _logger.LogDebug("Evicted {Count} expired keys", removed);
            }
        }
        catch (OperationCanceledException) { }
    }
}
