using Durum.Matrix.Server.Host;
using Durum.Matrix.Server.Host.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

// No dashboard: serve until stopped
WebApplication app = builder.Build();
app.MapMatrixServer();
if (!dashboard)
{
    app.Logger.LogInformation("Running without dashboard: {Reason}; --Host:Ui=Dashboard forces it", uiReason);
    await app.RunAsync();
    return;
}

// Dashboard: start serving first, so start-up errors such as a failed migration print to a normal terminal
await app.StartAsync();
using IApplication ui = Application.Create();
ui.Init();
using (ServerWindow window = ActivatorUtilities.CreateInstance<ServerWindow>(app.Services, ui)) ui.Run(window);
await app.StopAsync();
