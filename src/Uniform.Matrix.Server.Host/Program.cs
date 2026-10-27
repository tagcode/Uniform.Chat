using Uniform.Matrix.Server.Host;
using Uniform.Matrix.Server.Host.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Terminal.Gui.App;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;
using Uniform.Matrix.Server.Configuration;

// Read appsettings.json next to the executable, not from the working directory
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// Settings file and applied changes, then the server and this process's options
builder.AddSettings();
builder.AddMatrixServer();
builder.Services.AddOptionSection<ServerHostOptions>(builder.Configuration, ServerHostOptions.Section);
bool dashboard = (builder.Configuration.GetSection(ServerHostOptions.Section).Get<ServerHostOptions>() ?? new ServerHostOptions()).UseDashboard(out string uiReason);

// One log file per session; without the dashboard also to the console, which the dashboard owns otherwise
ServerStorageOptions storage = builder.Configuration.GetSection(ServerStorageOptions.Section).Get<ServerStorageOptions>() ?? new ServerStorageOptions();
builder.AddSessionLogging(storage.ResolveLogDirectory(builder.Environment.ApplicationName), storage.LogFilesKept);
if (!dashboard) builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
// The host's own start failure with its stack trace stays in the log file; the console gets the short message below
builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.Critical);
builder.Logging.AddFilter<ConsoleLoggerProvider>(StartupCategory, LogLevel.None);

// No dashboard: serve until stopped
WebApplication app = builder.Build();
app.MapMatrixServer();
if (!dashboard)
{
    app.Logger.LogInformation("Running without dashboard: {Reason}; --Host:Ui=Dashboard forces it", uiReason);
    if (!await StartAsync(app)) return 1;
    await app.WaitForShutdownAsync();
    await app.StopAsync();
    return 0;
}

// Dashboard: start serving first, so start-up errors such as a failed migration print to a normal terminal
if (!await StartAsync(app)) return 1;
using IApplication ui = Application.Create();
ui.Init();
using (ServerWindow window = ActivatorUtilities.CreateInstance<ServerWindow>(app.Services, ui)) ui.Run(window);
await app.StopAsync();
// Done
return 0;

// Start serving; when the listen address is taken, tell how to choose another instead of showing the exception, and return false
static async Task<bool> StartAsync(WebApplication app)
{
    // Other start-up errors keep their exception, e.g. a failed migration
    try
    {
        await app.StartAsync();
        return true;
    }
    catch (IOException exception) when (exception.InnerException is AddressInUseException)
    {
        string urls = app.Configuration["Urls"] ?? "http://localhost:8008";
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(StartupCategory).LogError("Cannot listen on {Urls}: the address is already in use", urls);
        Console.Error.WriteLine($"Cannot listen on {urls}: the address is already in use, perhaps by another copy of the server.");
        Console.Error.WriteLine("Stop that program, or choose another address with --Urls, e.g. --Urls=http://localhost:8009");
        // On error stop
        return false;
    }
}

/// <summary>The program's start-up messages.</summary>
internal partial class Program
{
    /// <summary>Log category of start-up failures that the console shows in its own words.</summary>
    private const string StartupCategory = "Uniform.Matrix.Server.Host.Startup";
}
