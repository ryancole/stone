using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BehaveDiff.Mcp;

public static class BehaveDiffMcpServer
{
    /// <summary>Runs the stdio MCP server until stdin closes. stdout carries only protocol messages.</summary>
    public static async Task RunAsync(BehaveDiffServerOptions options, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder();
        // Everything but protocol traffic goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(options);
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "behavediff", Version = typeof(BehaveDiffMcpServer).Assembly.GetName().Version?.ToString() ?? "0.0.0" })
            .WithStdioServerTransport()
            .WithTools<BehaveDiffTools>();

        await builder.Build().RunAsync(ct);
    }
}
