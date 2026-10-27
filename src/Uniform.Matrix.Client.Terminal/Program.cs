using Durum.Matrix.Client.Terminal;
using Durum.Matrix.Client.Terminal.Configuration;
using Durum.Matrix.Client.Terminal.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Terminal.Gui.App;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Core;

// Read appsettings.json next to the executable, not from the working directory
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

// User settings file below environment and command line, and changes applied in the settings window on top
builder.AddSettings();

// Assert a user ID before the terminal UI takes over the console; show help instead of a stack trace
string? userId = builder.Configuration[$"{MatrixOptions.Section}:{nameof(MatrixOptions.UserId)}"];
if (!UserId.TryParse(userId, out _))
{
    Console.Error.WriteLine(Usage.Text(userId, SettingsStore.DefaultPath(builder.Environment.ApplicationName)));
    Usage.WaitIfOwnConsole();
    return 2;
}

// Client services and options, then the terminal's own display options
builder.AddMatrixClient();
builder.Services.AddOptionSection<UiOptions>(builder.Configuration, UiOptions.Section);

// Terminal UI; created first so the password prompt can use it
using IApplication app = Application.Create();
app.Init();
builder.Services.AddSingleton(app);
builder.Services.AddSingleton<ICredentialPrompt, TuiCredentialPrompt>();

// Start services, run the UI until quit, then stop services
using IHost host = builder.Build();
using MainWindow window = ActivatorUtilities.CreateInstance<MainWindow>(host.Services);
await host.StartAsync();
app.Run(window);
await host.StopAsync();
// Done
return 0;
