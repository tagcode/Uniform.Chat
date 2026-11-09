namespace Uniform.Turn.Tests;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Uniform.Stun.Hosting;

/// <summary>Tests of <see cref="TurnServerHost"/> in a .NET host.</summary>
public class TurnHostingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Return a started host with <paramref name="settings"/> as command-line settings.</summary>
    private static async Task<IHost> StartAsync(params string[] settings)
    {
        // A plain host with the TURN server
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = settings, DisableDefaults = true });
        builder.AddTurnServer();
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        // Return host
        return host;
    }

    /// <summary>The server runs with the settings: fixed users relay to allowed loopback peers.</summary>
    [Fact]
    public async Task StartFromSettings()
    {
        // Arrange
        using IHost host = await StartAsync("--Turn:Enabled=true", "--Turn:ListenAddress=127.0.0.1", "--Turn:Port=0", "--Turn:MinRelayPort=41000", "--Turn:MaxRelayPort=41999", "--Turn:Users=carol:secret", "--Turn:AllowedPeers=127.0.0.0/8");
        TurnServerHost turn = host.Services.GetRequiredService<TurnServerHost>();

        // Act
        await using TurnClientEndpoint client = await TurnClientEndpoint.ConnectAsync(turn.Server!.UdpEndPoint!, "carol", "secret", cancellationToken: Ct);
        bool allocated = await client.Turn.AllocateAsync(Ct);
        bool permitted = await client.Turn.PermitAsync(IPAddress.Loopback, Ct);
        await host.StopAsync(Ct);

        // Assert
        Assert.Null(turn.Error);
        Assert.True(allocated);
        Assert.True(permitted);
        Assert.Null(turn.Server);
    }

    /// <summary>A host name or a STUN server as the external address is asked for the public address, here loopback.</summary>
    [Fact]
    public async Task FindExternalAddress()
    {
        // Arrange: a STUN server on loopback to ask
        using IHost stunHost = await StartAsync("--Turn:Enabled=true", "--Turn:ListenAddress=127.0.0.1", "--Turn:Port=0", "--Turn:Tcp=false", "--Turn:Users=a:b");
        int stunPort = stunHost.Services.GetRequiredService<TurnServerHost>().Server!.UdpEndPoint!.Port;

        // Act
        using IHost byName = await StartAsync("--Turn:Enabled=true", "--Turn:Port=0", "--Turn:Tcp=false", "--Turn:Users=a:b", "--Turn:MinRelayPort=42000", "--Turn:MaxRelayPort=42999", "--Turn:ExternalAddress=localhost");
        using IHost byStun = await StartAsync("--Turn:Enabled=true", "--Turn:Port=0", "--Turn:Tcp=false", "--Turn:Users=a:b", "--Turn:MinRelayPort=43000", "--Turn:MaxRelayPort=43999", $"--Turn:ExternalAddress=stun:127.0.0.1:{stunPort}");

        // Assert
        Assert.Equal(IPAddress.Loopback, byName.Services.GetRequiredService<TurnServerHost>().Server?.ExternalAddress);
        Assert.Equal(IPAddress.Loopback, byStun.Services.GetRequiredService<TurnServerHost>().Server?.ExternalAddress);
    }

    /// <summary>Settings that cannot work leave the server stopped with the reason, and the host running.</summary>
    [Fact]
    public async Task KeepHostOnBadSettings()
    {
        // Arrange and act
        using IHost noCredentials = await StartAsync("--Turn:Enabled=true", "--Turn:ListenAddress=127.0.0.1", "--Turn:Port=0");
        using IHost noExternal = await StartAsync("--Turn:Enabled=true", "--Turn:Port=0", "--Turn:Users=a:b");
        using IHost disabled = await StartAsync("--Turn:Users=a:b");

        // Assert
        Assert.Contains("credentials", noCredentials.Services.GetRequiredService<TurnServerHost>().Error);
        Assert.Contains("external address", noExternal.Services.GetRequiredService<TurnServerHost>().Error);
        Assert.Null(disabled.Services.GetRequiredService<TurnServerHost>().Server);
        Assert.Null(disabled.Services.GetRequiredService<TurnServerHost>().Error);
    }

    /// <summary>A certificate the user cannot read keeps the TURN server stopped with an error, and the host running, as a homeserver keeps serving without it.</summary>
    [Fact]
    public async Task KeepHostOnUnreadableCertificate()
    {
        // Arrange: certificate files nobody may read; root reads them anyway
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") { Assert.Skip("Needs a non-root user on Unix."); return; }
        string directory = Directory.CreateTempSubdirectory("turn-tls-").FullName;
        string certificate = Path.Combine(directory, "fullchain.pem"), key = Path.Combine(directory, "privkey.pem");
        File.WriteAllText(certificate, "unreadable");
        File.WriteAllText(key, "unreadable");
        File.SetUnixFileMode(certificate, UnixFileMode.None);
        File.SetUnixFileMode(key, UnixFileMode.None);

        try
        {
            // Act
            using IHost host = await StartAsync("--Turn:Enabled=true", "--Turn:ListenAddress=127.0.0.1", "--Turn:Port=0", "--Turn:TlsPort=0", "--Turn:Users=a:b", $"--Turn:CertificatePath={certificate}", $"--Turn:KeyPath={key}");

            // Assert
            TurnServerHost turn = host.Services.GetRequiredService<TurnServerHost>();
            Assert.Null(turn.Server);
            Assert.Contains("cannot read", turn.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
