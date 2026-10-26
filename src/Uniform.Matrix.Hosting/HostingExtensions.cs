namespace Uniform.Matrix.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Registers settings, options and session logging on a .NET host, the same way for every application.</summary>
/// <example><code><![CDATA[
/// HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
/// builder.AddSettings();
/// builder.AddSessionLogging(logDirectory, keep: 20);
/// builder.Services.AddOptionSection<UiOptions>(builder.Configuration, "Ui");
/// ]]></code></example>
public static class HostingExtensions
{
    /// <summary>Add the user's settings file of the application and the applied layer to the configuration, and register <see cref="SettingsStore"/> and <see cref="OptionCatalog"/>.</summary>
    /// <remarks>The settings file is <see cref="SettingsStore.DefaultPath"/> of the host's application name.</remarks>
    public static IHostApplicationBuilder AddSettings(this IHostApplicationBuilder builder)
    {
        // Layers, then the services that edit them
        string path = SettingsStore.DefaultPath(builder.Environment.ApplicationName);
        AppliedConfigurationProvider applied = SettingsStore.AddLayers(builder.Configuration, path);
        builder.Services.AddSingleton(new SettingsStore((IConfigurationRoot)builder.Configuration, applied, path));
        builder.Services.AddSingleton(services => new OptionCatalog(services.GetServices<OptionSection>()));
        // Return builder
        return builder;
    }

    /// <summary>Replace the log providers with one file per session in <paramref name="directory"/>, keeping <paramref name="keep"/> files, and register the <see cref="FileLoggerProvider"/>.</summary>
    public static IHostApplicationBuilder AddSessionLogging(this IHostApplicationBuilder builder, string directory, int keep)
    {
        // The terminal may belong to a UI, so the file is the only log
        FileLoggerProvider provider = new(directory, keep);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(provider);
        builder.Services.AddSingleton(provider);
        // Return builder
        return builder;
    }

    /// <summary>Bind <typeparamref name="T"/> to configuration section <paramref name="section"/> and list its properties in the <see cref="OptionCatalog"/>.</summary>
    public static IServiceCollection AddOptionSection<T>(this IServiceCollection services, IConfiguration configuration, string section) where T : class
    {
        // Bind and register for the catalog
        services.Configure<T>(configuration.GetSection(section));
        services.AddSingleton(new OptionSection(section, typeof(T)));
        // Return services
        return services;
    }
}
