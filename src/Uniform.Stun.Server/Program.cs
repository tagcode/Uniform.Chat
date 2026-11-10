using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Hosting;
using Uniform.Stun.Hosting;

// Read appsettings.json next to the executable; the application name names the folders
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory, ApplicationName = ApplicationName });

// Settings file and the server
builder.AddSettings();
builder.AddTurnServer();

// One log file per session, and the console
builder.AddSessionLogging(Path.Combine(AppDirectories.Data(ApplicationName), "logs"), 20, "turn");
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);

// Start; settings that cannot work end the program with their reason
using IHost host = builder.Build();
await host.StartAsync();
TurnServerHost turn = host.Services.GetRequiredService<TurnServerHost>();
if (turn.Server is null)
{
    Console.Error.WriteLine(turn.Error ?? "The TURN server is disabled: set Turn:Enabled=true.");
    Console.Error.WriteLine($"Settings are in {SettingsStore.DefaultPath(ApplicationName)}, or give them on the command line, e.g. --Turn:ExternalAddress=203.0.113.5 --Turn:SharedSecrets=a-long-secret");
    await host.StopAsync();
    return 1;
}

// Serve until stopped with Ctrl+C or SIGTERM
await host.WaitForShutdownAsync();
// Done
return 0;

/// <summary>The program's names.</summary>
internal partial class Program
{
    /// <summary>Application name, which names the settings and data folders: <c>~/.config/uniform-stun-server</c> and <c>~/.local/share/uniform-stun-server</c> on Linux.</summary>
    private const string ApplicationName = "uniform-stun-server";
}
