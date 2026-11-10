namespace Uniform.Chat.Server;

using Uniform.Matrix.Hosting;

/// <summary>How the server process presents itself.</summary>
public enum UiMode
{
    /// <summary>The dashboard when input and output are a terminal; none when either is redirected, as under a service manager.</summary>
    Auto,
    /// <summary>Dashboard with settings and logs, drawn in the terminal.</summary>
    Dashboard,
    /// <summary>No UI; logs also go to the console, e.g. for systemd's journal.</summary>
    None,
}

/// <summary>Process settings of the server application, configuration section <see cref="Section"/>.</summary>
public sealed record ServerHostOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Host";

    /// <summary>The admin socket of the system service, which its unit sets and <c>uniform-chat-server manage</c> opens by default.</summary>
    public const string ServiceAdminSocket = "/run/uniform-chat-server/admin.sock";

    /// <summary>Whether to run with the dashboard: <see cref="UiMode.Auto"/>, <see cref="UiMode.Dashboard"/> or <see cref="UiMode.None"/>.</summary>
    [RestartRequired]
    public UiMode Ui { get; set; } = UiMode.Auto;

    /// <summary>A local Unix socket for managing the running server, e.g. <c>/run/uniform-chat-server/admin.sock</c>: whoever may open it, by its file permissions, manages the server with <c>uniform-chat-server manage</c> as an admin, without an account. The socket is made readable and writable by the server's user and group only. Empty for none.</summary>
    [RestartRequired]
    public string? AdminSocket { get; set; }

    /// <summary>Return whether to show the dashboard: forced by <see cref="Ui"/>, or for <see cref="UiMode.Auto"/> when input and output are a terminal; <paramref name="reason"/> says why.</summary>
    /// <remarks>Service managers such as systemd connect input to <c>/dev/null</c> and output to a log, so they count as redirected. Environment variables such as <c>INVOCATION_ID</c> are not used, as terminals started by a systemd user session inherit them.</remarks>
    public bool UseDashboard(out string reason)
    {
        // Forced modes
        reason = $"{nameof(Ui)} is {Ui}";
        if (Ui == UiMode.Dashboard) return true;
        if (Ui == UiMode.None) return false;

        // Auto: both ends must be a terminal
        reason = Console.IsInputRedirected ? "input is redirected" : Console.IsOutputRedirected ? "output is redirected" : "input and output are a terminal";
        // Return choice
        return !Console.IsInputRedirected && !Console.IsOutputRedirected;
    }
}
