using Uniform.Matrix.Client.Terminal;
using Uniform.Matrix.Client.Terminal.Configuration;
using Uniform.Matrix.Client.Terminal.Ui;
using Microsoft.Extensions.Configuration;
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

// Help when asked, and without a user ID and a console to ask in, instead of a stack trace
string userIdKey = $"{MatrixOptions.Section}:{nameof(MatrixOptions.UserId)}";
string homeserverKey = $"{MatrixOptions.Section}:{nameof(MatrixOptions.Homeserver)}";
string? userId = builder.Configuration[userIdKey];
bool hasUserId = UserId.TryParse(userId, out _);
bool askedForHelp = Usage.IsHelp(args);
if (askedForHelp || !hasUserId && !Usage.CanAsk)
{
    OptionCatalog settings = new([new OptionSection(MatrixOptions.Section, typeof(MatrixOptions)), new OptionSection(StorageOptions.Section, typeof(StorageOptions)), new OptionSection(UiOptions.Section, typeof(UiOptions)), new OptionSection(AudioOptions.Section, typeof(AudioOptions))]);
    (askedForHelp ? Console.Out : Console.Error).WriteLine(Usage.Text(userId, askedForHelp, SettingsStore.DefaultPath(builder.Environment.ApplicationName), [.. settings.GetAll(builder.Configuration).Where(o => o.Key == "Logging:LogLevel:Default" || !o.Key.StartsWith("Logging:"))]));
    Usage.WaitIfOwnConsole();
    return askedForHelp ? 0 : 2;
}

// Terminal UI; created first so the first sign-in and the password prompt can use it
using IApplication app = Application.Create();
app.Init();
builder.Services.AddSingleton(app);

// First start: ask for the user ID in the window; it is saved once the login works
(string UserId, string? Homeserver)? entered = null;
if (!hasUserId)
{
    entered = ClientDialogs.AskSignIn(app, userId, builder.Configuration[homeserverKey]);
    if (entered is not var (enteredUserId, enteredHomeserver)) return 0;
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [userIdKey] = enteredUserId, [homeserverKey] = enteredHomeserver ?? builder.Configuration[homeserverKey] });
}

// Client services and options, then the terminal's own display and audio options
builder.AddMatrixClient();
builder.Services.AddOptionSection<UiOptions>(builder.Configuration, UiOptions.Section);
builder.Services.AddOptionSection<AudioOptions>(builder.Configuration, AudioOptions.Section);
builder.Services.AddSingleton<ICredentialPrompt, TuiCredentialPrompt>();

// Start services, run the UI until quit, then stop services
using IHost host = builder.Build();
using MainWindow window = ActivatorUtilities.CreateInstance<MainWindow>(host.Services);
await host.StartAsync();
if (entered is var (signedInUserId, signedInHomeserver)) _ = RememberSignInAsync(host.Services, app, userIdKey, signedInUserId, homeserverKey, signedInHomeserver);
app.Run(window);
await host.StopAsync();
// Done
return 0;

// Save the user ID and server address from the first sign-in to the settings file once the login works, so the next start needs no options
static async Task RememberSignInAsync(IServiceProvider services, IApplication app, string userIdKey, string userId, string homeserverKey, string? homeserver)
{
    // A failed or cancelled login saves nothing
    try
    {
        await services.GetRequiredService<ClientSession>().WhenReady;
    }
    catch (Exception)
    {
        return;
    }

    // Save on the UI thread, as the settings window uses the same store
    Dictionary<string, string?> values = new() { [userIdKey] = userId };
    if (homeserver is not null) values[homeserverKey] = homeserver;
    app.Invoke(() => services.GetRequiredService<SettingsStore>().Save(values));
}
