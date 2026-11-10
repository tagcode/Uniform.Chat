namespace Uniform.Chat.Server.Management;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;
using Uniform.Stun.Hosting;

/// <summary>The settings file of a server that runs in another process, e.g. the service a manager attached to: its options, as the server lists them, over that file.</summary>
/// <remarks>The values shown are the file's over the program's defaults; a service may also set some on its command line or in its environment, which only it sees. Saved changes take effect when the server starts again.</remarks>
internal static class ManagedSettings
{
    /// <summary>Return an editor of the settings file at <paramref name="path"/>.</summary>
    public static SettingsEditor Open(string path)
    {
        // The program's defaults, then the file, as the server layers them
        ConfigurationBuilder builder = new();
        builder.SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true);
        AppliedConfigurationProvider applied = SettingsStore.AddLayers(builder, path);
        IConfigurationRoot configuration = builder.Build();

        // The server's options in its order: its own, the embedded TURN server's, the host's
        ServiceCollection services = new();
        services.AddServerOptionSections(configuration);
        services.AddOptionSection<TurnOptions>(configuration, TurnOptions.Section);
        services.AddOptionSection<ServerHostOptions>(configuration, ServerHostOptions.Section);
        using ServiceProvider provider = services.BuildServiceProvider();
        OptionCatalog catalog = new(provider.GetServices<OptionSection>());
        // Return editor
        return new SettingsEditor(new SettingsStore(configuration, applied, path), catalog.GetAll(configuration));
    }
}
