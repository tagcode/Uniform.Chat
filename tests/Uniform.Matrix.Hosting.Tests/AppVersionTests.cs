namespace Uniform.Matrix.Hosting.Tests;

using System.Text.RegularExpressions;

/// <summary>Tests of <see cref="AppVersion"/>.</summary>
public sealed class AppVersionTests
{
    /// <summary>Show a version with the short commit it was built from, as the build stamps them.</summary>
    [Fact]
    public void ShowVersionAndCommit()
    {
        // This build's, e.g. 0.1.0 (6078a19)
        string version = AppVersion.Of(typeof(AppVersion).Assembly);
        // Done
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(\.\d+)?( \([0-9a-f]{7}\))?$"), version);
    }
}
