using RedisSharp.Core.Protocol;

namespace RedisSharp.Core.Commands;

/// <summary>
/// Routes an incoming RESP array to the matching <see cref="ICommand"/>,
/// validating arity and translating exceptions into RESP errors so a single
/// bad request never takes down the connection.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly Dictionary<string, ICommand> _commands;
    private readonly CommandContext _context;

    public CommandDispatcher(CommandContext context, IEnumerable<ICommand> commands)
    {
        _context = context;
        _commands = commands.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates a dispatcher wired with every built-in command.</summary>
    public static CommandDispatcher CreateDefault(CommandContext context) =>
        new(context, BuiltInCommands.All());

    public IReadOnlyCollection<string> CommandNames => _commands.Keys;

    public RespValue Dispatch(RespValue request)
    {
        if (request is not RespArray { Items.Count: > 0 } array)
            return RespValue.Error("ERR protocol error: expected a non-empty array");

        var args = new string[array.Items.Count];
        for (var i = 0; i < args.Length; i++)
        {
            var s = array.Items[i].AsString();
            if (s is null)
                return RespValue.Error("ERR protocol error: expected bulk string arguments");
            args[i] = s;
        }

        if (!_commands.TryGetValue(args[0], out var command))
            return RespValue.Error($"ERR unknown command '{args[0]}', with args beginning with: {FormatArgs(args)}");

        if (!ArityMatches(command.Arity, args.Length))
            return RespValue.Error($"ERR wrong number of arguments for '{command.Name.ToLowerInvariant()}' command");

        try
        {
            return command.Execute(_context, args);
        }
        catch (CommandException ex)
        {
            return RespValue.Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return RespValue.Error("ERR " + ex.Message);
        }
    }

    private static bool ArityMatches(int arity, int actual) =>
        arity >= 0 ? actual == arity : actual >= -arity;

    private static string FormatArgs(string[] args) =>
        string.Join(" ", args.Skip(1).Take(3).Select(a => $"'{a}'"));
}

/// <summary>Thrown by commands to send a specific RESP error (message should already carry its prefix, e.g. "ERR ...").</summary>
public sealed class CommandException(string message) : Exception(message);
