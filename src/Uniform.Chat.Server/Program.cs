using Uniform.Chat.Server;
using Uniform.Chat.Server.Ui;
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
using Uniform.Matrix.Server.Http;

// Read appsettings.json next to the executable, not from the working directory; the application name keeps the old folders
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, ApplicationName = ApplicationName });

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

// A bad TLS certificate stops the build, before logging works; tell it in short
WebApplication app;
try
{
    app = builder.Build();
}
catch (Exception exception) when (FindTlsError(exception) is TlsCertificateException tls)
{
    WriteTlsError(tls);
    return 1;
}

// No dashboard: serve until stopped
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
    catch (Exception exception) when (FindTlsError(exception) is TlsCertificateException tls)
    {
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(StartupCategory).LogError("{Error}", tls.Message);
        WriteTlsError(tls);
        // On error stop
        return false;
    }
}

// Tell what is wrong with the certificate, and how the paths look
static void WriteTlsError(TlsCertificateException tls)
{
    // Message, then an example
    Console.Error.WriteLine(tls.Message);
    Console.Error.WriteLine("Check the paths, e.g. --Tls:CertificatePath=/etc/letsencrypt/live/example.org/fullchain.pem --Tls:KeyPath=/etc/letsencrypt/live/example.org/privkey.pem");
}

// Return the TLS certificate error inside an exception from starting, as Kestrel wraps it; null for other errors
static TlsCertificateException? FindTlsError(Exception exception)
{
    // Down the inner exceptions
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current is TlsCertificateException tls) return tls;
    }
    // Return none
    return null;
}

/// <summary>The program's start-up messages.</summary>
internal partial class Program
{
    /// <summary>Log category of start-up failures that the console shows in its own words.</summary>
    private const string StartupCategory = "Uniform.Chat.Server.Startup";

    /// <summary>Application name, which names the settings, data and log folders, e.g. <c>~/.local/share/uniform-matrix-server-host</c>; kept from before the program was renamed, so that existing servers keep their data.</summary>
    private const string ApplicationName = "Uniform.Matrix.Server.Host";
}
