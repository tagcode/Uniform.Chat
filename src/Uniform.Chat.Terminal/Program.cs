using Uniform.Matrix.Calls;
using Uniform.Chat.Terminal;
using Uniform.Chat.Terminal.Configuration;
using Uniform.Chat.Terminal.Ui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Terminal.Gui.App;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Core;

// Read appsettings.json next to the executable, not from the working directory; the ApplicationName names the settings and data folders, not the program file
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory, ApplicationName = "Uniform.Chat.Terminal" });

// User settings file below environment and command line, and changes applied in the settings window on top
builder.AddSettings();

// Help when asked, and without a user ID and a console to ask in, instead of a stack trace
string userIdKey = $"{MatrixOptions.Section}:{nameof(MatrixOptions.UserId)}";
string homeserverKey = $"{MatrixOptions.Section}:{nameof(MatrixOptions.Homeserver)}";
string? userId = builder.Configuration[userIdKey];
bool hasUserId = UserId.TryParse(userId, out _);
bool askedForHelp = Usage.IsHelp(args);

// Without a terminal, e.g. double-clicked in a file manager: open one, or end instead of running with nowhere to draw
if (!askedForHelp && !Usage.CanAsk)
{
    if (TerminalWindow.TryReopen(args)) return 0;
    if (hasUserId)
    {
        Console.Error.WriteLine("Uniform Chat is a terminal app: start it in a terminal.");
        return 2;
    }
}
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

// First start: ask for the user ID in the window, creating the account when asked; it is saved once the login works
(string UserId, string? Homeserver)? entered = null;
while (!hasUserId)
{
    if (ClientDialogs.AskSignIn(app, userId, builder.Configuration[homeserverKey]) is not var (enteredUserId, enteredHomeserver, createAccount)) return 0;
    string? newPassword = null;
    if (createAccount && (newPassword = ClientDialogs.CreateAccount(app, enteredUserId, enteredHomeserver ?? builder.Configuration[homeserverKey])) is null)
    {
        // Back to the form
        userId = enteredUserId;
        continue;
    }
    entered = (enteredUserId, enteredHomeserver);
    Dictionary<string, string?> signIn = new() { [userIdKey] = enteredUserId, [homeserverKey] = enteredHomeserver ?? builder.Configuration[homeserverKey] };
    if (newPassword is not null) signIn[$"{MatrixOptions.Section}:{nameof(MatrixOptions.Password)}"] = newPassword;
    builder.Configuration.AddInMemoryCollection(signIn);
    break;
}

// Client services and options, then the terminal's own display and audio options
builder.AddMatrixClient();
builder.Services.AddOptionSection<UiOptions>(builder.Configuration, UiOptions.Section);
builder.Services.AddOptionSection<AudioOptions>(builder.Configuration, AudioOptions.Section);
builder.Services.AddSingleton<ICredentialPrompt, TuiCredentialPrompt>();
builder.Services.AddSingleton<CallService>();

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
