using Uniform.Chat.Server;
using Uniform.Chat.Server.Management;
using Uniform.Chat.Server.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Terminal.Gui.App;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Storage;

// The manager of a running server needs no server of its own
if (args.Length > 0 && args[0] == ManageCommand.Name) return await ManageCommand.RunAsync(args[1..]);

// Read appsettings.json next to the executable, not from the working directory; the application name names the folders
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, ApplicationName = ApplicationName });

// Settings file and applied changes, then the server and this process's options; one process per data directory
builder.AddSettings();
try
{
    builder.AddMatrixServer();
}
catch (DataDirectoryInUseException inUse)
{
    Console.Error.WriteLine(inUse.Message);
    Console.Error.WriteLine("As a system service, stop it with: sudo systemctl stop uniform-chat-server");
    return 1;
}
builder.AddEmbeddedTurn();
builder.Services.AddOptionSection<ServerHostOptions>(builder.Configuration, ServerHostOptions.Section);
// Stopping, with its clean-up, ends well before a service manager kills the process, e.g. systemd after the unit's 30 seconds
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(20));
ServerHostOptions host = builder.Configuration.GetSection(ServerHostOptions.Section).Get<ServerHostOptions>() ?? new ServerHostOptions();
bool dashboard = host.UseDashboard(out string uiReason);
string? adminSocket = string.IsNullOrWhiteSpace(host.AdminSocket) ? null : Path.GetFullPath(host.AdminSocket);
if (adminSocket is not null) AddAdminSocket(builder, adminSocket);

// One log file per session; without the dashboard also to the console, which the dashboard owns otherwise
ServerStorageOptions storage = builder.Configuration.GetSection(ServerStorageOptions.Section).Get<ServerStorageOptions>() ?? new ServerStorageOptions();
builder.AddSessionLogging(storage.ResolveLogDirectory(builder.Environment.ApplicationName), storage.LogFilesKept, "server");
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

// No dashboard: serve until stopped; a slow stop shows between the two log lines
app.MapMatrixServer();
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Stopping…"));
if (!dashboard)
{
    app.Logger.LogInformation("Running without dashboard: {Reason}; --Host:Ui=Dashboard forces it", uiReason);
    if (!await StartAsync(app)) return await StopAsync(app, 1);
    RestrictAdminSocket(adminSocket);
    await app.WaitForShutdownAsync();
    return await StopAsync(app, 0);
}

// Dashboard: start serving first, so start-up errors such as a failed migration print to a normal terminal
if (!await StartAsync(app)) return await StopAsync(app, 1);
RestrictAdminSocket(adminSocket);
using IApplication ui = Application.Create();
ui.Init();
SettingsStore settings = app.Services.GetRequiredService<SettingsStore>();
DashboardOptions options = new() { OpenSettings = () => new SettingsEditor(settings, app.Services.GetRequiredService<OptionCatalog>().GetAll(app.Configuration)) };
using (ServerWindow window = new(ui, ActivatorUtilities.CreateInstance<LocalServerAdmin>(app.Services), options, TimeProvider.System)) ui.Run(window);
// Done
return await StopAsync(app, 0);

// Serve the admin socket next to the configured addresses, replacing a file a stopped server left; the data directory lock keeps another server of the same data from using it
static void AddAdminSocket(WebApplicationBuilder builder, string path)
{
    // A fresh socket in its folder
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (File.Exists(path)) File.Delete(path);

    // One more address, and its connections marked as the admin's
    string urls = builder.Configuration["Urls"] ?? "http://localhost:5000";
    builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, $"{urls};http://unix:{path}");
    // Done
    builder.Services.Configure<KestrelServerOptions>(kestrel => kestrel.MarkAdminSocket(path));
}

// Let only the server's user and group open the admin socket, as Kestrel creates it with the process's umask
static void RestrictAdminSocket(string? path)
{
    // Unix only
    if (path is null || OperatingSystem.IsWindows()) return;
    // Done
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
}

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

// Stop the server, log that it stopped, and release its data directory and log files; return the exit code
static async Task<int> StopAsync(WebApplication app, int exitCode)
{
    // Stop within the host's shutdown timeout, then dispose, which flushes the log
    await app.StopAsync();
    app.Logger.LogInformation("Stopped");
    await app.DisposeAsync();
    // Return code
    return exitCode;
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

    /// <summary>Application name, which names the settings, data and log folders: <c>~/.config/uniform-chat-server</c> and <c>~/.local/share/uniform-chat-server</c> on Linux; ASP.NET also loads the assembly of this name at start, so it is the program's assembly name.</summary>
    private const string ApplicationName = "uniform-chat-server";
}
