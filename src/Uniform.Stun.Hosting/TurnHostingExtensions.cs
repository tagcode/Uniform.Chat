namespace Uniform.Stun.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Uniform.Matrix.Hosting;

/// <summary>Adds the STUN and TURN server to a .NET host.</summary>
/// <example><code><![CDATA[
/// HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
/// builder.AddTurnServer(); // runs when Turn:Enabled is true
/// await builder.Build().RunAsync();
/// ]]></code></example>
public static class TurnHostingExtensions
{
    /// <summary>Bind section <see cref="TurnOptions.Section"/>, list it among the settings, and run a <see cref="TurnServerHost"/>; register an <see cref="Uniform.Turn.ITurnAuthenticator"/> to add credentials of the host.</summary>
    public static IHostApplicationBuilder AddTurnServer(this IHostApplicationBuilder builder)
    {
        // Options, then the host as a service and a hosted service
        builder.Services.AddOptionSection<TurnOptions>(builder.Configuration, TurnOptions.Section);
        builder.Services.AddSingleton<TurnServerHost>();
        builder.Services.AddHostedService(services => services.GetRequiredService<TurnServerHost>());
        // Return builder
        return builder;
    }
}
