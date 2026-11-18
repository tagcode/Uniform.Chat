using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;
using Uniform.MatrixRtc.Hosting;
using Uniform.MatrixRtc.Server;
using Uniform.Matrix.Hosting;

// Read appsettings.json next to the executable; the application name names the folders
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, ApplicationName = ApplicationName });

// Settings file, then the MATRIXRTC_ environment variables above it and below the own environment and command line
builder.AddSettings();
IConfigurationBuilder sources = builder.Configuration;
int above = sources.Sources.ToList().FindLastIndex(source => source is JsonConfigurationSource) + 1;
sources.Sources.Insert(above, new MemoryConfigurationSource { InitialData = CompatibleEnvironment.Read(Environment.GetEnvironmentVariable) });
builder.AddLiveKitAuthorization();

// One log file per session, and the console
builder.AddSessionLogging(Path.Combine(AppDirectories.Data(ApplicationName), "logs"), 20, "livekit");
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);

// Settings that cannot work end the program with their reason
MatrixRtcOptions options = builder.Configuration.GetSection(MatrixRtcOptions.Section).Get<MatrixRtcOptions>() ?? new MatrixRtcOptions();
if (CompatibleEnvironment.Missing(options) is string missing)
{
    Console.Error.WriteLine($"The MatrixRTC authorization service needs {missing}.");
    Console.Error.WriteLine($"Settings are in {SettingsStore.DefaultPath(ApplicationName)}, or set MatrixRtc:Url, MatrixRtc:ApiKey and MatrixRtc:ApiSecret there, or MATRIXRTC_URL, MATRIXRTC_KEY and MATRIXRTC_SECRET in the environment.");
    return 1;
}

// Serve at the root, as lk-jwt-service does, until stopped with Ctrl+C or SIGTERM
WebApplication app = builder.Build();
app.MapLiveKitAuthorization();
await app.RunAsync();
// Done
return 0;

/// <summary>The program's names.</summary>
internal partial class Program
{
    /// <summary>Application name, which names the settings and data folders: <c>~/.config/uniform-matrixrtc-server</c> and <c>~/.local/share/uniform-matrixrtc-server</c> on Linux.</summary>
    private const string ApplicationName = "uniform-matrixrtc-server";
}
