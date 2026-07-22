using GlassCoder.Core.Agent;
using GlassCoder.Core.Hosting;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Headless entry point (CLAUDE.md §17). Task 3 wires the shared bootstrap and proves the
// services resolve; task 30 turns this into the real CI surface - config path, repo root and
// meaningful exit codes.
string? configPath = GetOption(args, "--config");

HostApplicationBuilder builder = GlassCoderHost.CreateBuilder(args, configPath);
using IHost host = builder.Build();
await host.StartAsync().ConfigureAwait(false);

IToolRegistry tools = host.Services.GetRequiredService<IToolRegistry>();
IAgentLoop loop = host.Services.GetRequiredService<IAgentLoop>();

Console.WriteLine($"GlassCoder host ready. {tools.Functions.Count} tools registered: {string.Join(", ", tools.Functions.Select(f => f.Name))}.");
Console.WriteLine($"Controller loop: {loop.GetType().Name}. Working directory: {Environment.CurrentDirectory}.");

await host.StopAsync().ConfigureAwait(false);
return 0;

static string? GetOption(string[] arguments, string name)
{
    int index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
