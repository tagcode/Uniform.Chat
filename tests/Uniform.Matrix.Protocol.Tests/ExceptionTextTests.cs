namespace Uniform.Matrix.Protocol.Tests;

using System.Security.Authentication;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of <see cref="ExceptionText"/>.</summary>
public sealed class ExceptionTextTests
{
    /// <summary>Describe an exception with the messages of its inner ones, as an SSL failure hides its reason inside.</summary>
    [Fact]
    public void DescribeInnerExceptions()
    {
        // An HTTP failure around an authentication failure
        HttpRequestException failure = new("The SSL connection could not be established, see inner exception.", new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

        // Done
        Assert.Equal("The SSL connection could not be established, see inner exception. → The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot", failure.Describe());
        Assert.Equal("plain", new InvalidOperationException("plain").Describe());
    }
}
