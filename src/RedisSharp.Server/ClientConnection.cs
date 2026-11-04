using System.IO.Pipelines;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RedisSharp.Core.Commands;
using RedisSharp.Core.Protocol;

namespace RedisSharp.Server;

/// <summary>
/// Handles one TCP client: reads RESP frames off a <see cref="PipeReader"/>,
/// dispatches them, and writes replies back. Pipelined requests are supported
/// naturally because the parse loop drains every complete frame in the buffer.
/// </summary>
public sealed class ClientConnection
{
    private readonly Socket _socket;
    private readonly CommandDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly string _remote;

    public ClientConnection(Socket socket, CommandDispatcher dispatcher, ILogger logger)
    {
        _socket = socket;
        _dispatcher = dispatcher;
        _logger = logger;
        _remote = socket.RemoteEndPoint?.ToString() ?? "unknown";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Client connected: {Remote}", _remote);

        await using var stream = new NetworkStream(_socket, ownsSocket: true);
        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                try
                {
                    while (RespParser.TryParse(buffer, out var request, out var consumed))
                    {
                        var reply = _dispatcher.Dispatch(request!);
                        RespWriter.Write(writer, reply);
                        buffer = buffer.Slice(consumed);
                    }
                }
                catch (RespProtocolException ex)
                {
                    RespWriter.Write(writer, RespValue.Error("ERR Protocol error: " + ex.Message));
                    await writer.FlushAsync(ct);
                    break;
                }

                await writer.FlushAsync(ct);
                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            _logger.LogDebug("Client {Remote} dropped: {Message}", _remote, ex.Message);
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
            _logger.LogInformation("Client disconnected: {Remote}", _remote);
        }
    }
}
