namespace Uniform.Matrix.Server.Configuration;

using Uniform.Matrix.Hosting;

/// <summary>HTTPS with a certificate from PEM files, such as those of certbot, configuration section <see cref="Section"/>; the <c>https://</c> addresses of <c>Urls</c> use it.</summary>
/// <remarks>The files are read again when they change, so renewed certificates are used without a restart.</remarks>
public sealed record TlsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Tls";

    /// <summary>Certificate file with its intermediates, e.g. certbot's <c>fullchain.pem</c>; unset for plain HTTP.</summary>
    [RestartRequired]
    public string? CertificatePath { get; set; }
    /// <summary>Private key file of the certificate, e.g. certbot's <c>privkey.pem</c>.</summary>
    [RestartRequired]
    public string? KeyPath { get; set; }
}
