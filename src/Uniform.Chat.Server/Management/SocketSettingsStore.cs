namespace Uniform.Chat.Server.Management;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;
using Uniform.Stun.Hosting;

/// <summary>The settings of a running server, read and changed through its admin socket: applied changes take effect in the server at once, and saved ones go to its settings file, which the server writes itself.</summary>
/// <remarks>Values are those the server runs with, from every layer, also its command line and environment; secrets never leave the server and show as hidden. Each change is checked by the server.</remarks>
internal sealed class SocketSettingsStore : ISettingsStore
{
    private readonly HttpServerAdmin admin;
    private Dictionary<string, (string? Value, string Source)> values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Use <paramref name="admin"/>'s socket, starting from <paramref name="settings"/> it returned.</summary>
    private SocketSettingsStore(HttpServerAdmin admin, JsonObject settings)
    {
        // Values of the first read
        this.admin = admin;
        Path = settings["settings_file"]?.GetValue<string>() ?? "";
        Take(settings);
    }

    /// <summary>The server's settings file.</summary>
    public string Path { get; }

    /// <summary>Return an editor of the server's settings: the options this program knows, which the server also has, over the server's values.</summary>
    /// <exception cref="IOException">When the server does not answer.</exception>
    public static SettingsEditor Open(HttpServerAdmin admin)
    {
        // The server's values, then the options both know
        SocketSettingsStore store = new(admin, Call(() => admin.GetSettingsAsync(CancellationToken.None)));
        IReadOnlyList<OptionInfo> options = [.. Options().Where(o => store.values.ContainsKey(o.Key))];
        // Return editor
        return new SettingsEditor(store, options);
    }

    /// <inheritdoc />
    public string? GetValue(string key) => values.TryGetValue(key, out (string? Value, string Source) entry) ? entry.Value : null;

    /// <inheritdoc />
    public string GetSource(string key) => values.TryGetValue(key, out (string? Value, string Source) entry) ? entry.Source : "default";

    /// <summary>Apply the changes in the server, until it restarts.</summary>
    public void Apply(IReadOnlyDictionary<string, string?> changes) => Take(Call(() => admin.ChangeSettingsAsync("apply", changes, CancellationToken.None)));

    /// <summary>Save the changes to the server's settings file, which also applies them there.</summary>
    public void Save(IReadOnlyDictionary<string, string?> changes) => Take(Call(() => admin.ChangeSettingsAsync("save", changes, CancellationToken.None)));

    /// <summary>Keep the values of an answer.</summary>
    private void Take(JsonObject settings)
    {
        // Value and source by key
        Dictionary<string, (string?, string)> read = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, JsonNode? entry) in settings["values"] as JsonObject ?? []) read[key] = (entry?["value"]?.GetValue<string>(), entry?["source"]?.GetValue<string>() ?? "default");
        // Done
        values = read;
    }

    /// <summary>Return the options of this program's server in their order: its own, the embedded TURN server's, the host's.</summary>
    private static IReadOnlyList<OptionInfo> Options()
    {
        // Defaults as the program has them
        IConfigurationRoot configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).Build();
        ServiceCollection services = new();
        services.AddServerOptionSections(configuration);
        services.AddOptionSection<TurnOptions>(configuration, TurnOptions.Section);
        services.AddOptionSection<ServerHostOptions>(configuration, ServerHostOptions.Section);
        using ServiceProvider provider = services.BuildServiceProvider();
        // Return options
        return new OptionCatalog(provider.GetServices<OptionSection>()).GetAll(configuration);
    }

    /// <summary>Run a call to the server from the UI thread and wait for it; a server that does not answer or refuses becomes an <see cref="IOException"/>, which the settings window shows.</summary>
    private static JsonObject Call(Func<Task<JsonObject>> call)
    {
        // On the thread pool, so continuations do not need the waiting thread
        try
        {
            return Task.Run(call).GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            // On a server that went away
            throw new IOException($"The server did not answer: {ex.Message}", ex);
        }
    }
}
