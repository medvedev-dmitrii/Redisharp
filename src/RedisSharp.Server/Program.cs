using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RedisSharp.Core.Commands;
using RedisSharp.Core.Storage;
using RedisSharp.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<KeyValueStore>();
builder.Services.AddSingleton(sp => new ServerInfo(
    Version: typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev",
    StartedAt: sp.GetRequiredService<IClock>().UtcNow));
builder.Services.AddSingleton(sp => new CommandContext(
    sp.GetRequiredService<KeyValueStore>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<ServerInfo>()));
builder.Services.AddSingleton(sp => CommandDispatcher.CreateDefault(sp.GetRequiredService<CommandContext>()));

builder.Services.AddHostedService<RedisServer>();
builder.Services.AddHostedService<ExpirySweeper>();

await builder.Build().RunAsync();

public partial class Program;
