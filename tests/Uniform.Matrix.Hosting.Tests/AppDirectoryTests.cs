namespace Uniform.Matrix.Hosting.Tests;

/// <summary>Tests of <see cref="AppDirectories"/>.</summary>
public class AppDirectoryTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>A leading <c>~</c> becomes the home directory, as a shell would do.</summary>
    [Theory]
    [InlineData("~/.config/tls/key.pem", ".config/tls/key.pem")]
    [InlineData("~", "")]
    public void ExpandHome(string path, string underHome)
    {
        // Act and assert
        Assert.Equal(Path.Join(Home, underHome), AppDirectories.ExpandHome(path));
    }

    /// <summary>Paths without a leading <c>~/</c>, and other users' homes, are not changed.</summary>
    [Theory]
    [InlineData("/etc/letsencrypt/live/x/privkey.pem")]
    [InlineData("relative/key.pem")]
    [InlineData("~bob/key.pem")]
    [InlineData("a/~/key.pem")]
    public void KeepOtherPaths(string path)
    {
        // Act and assert
        Assert.Equal(path, AppDirectories.ExpandHome(path));
    }
}
