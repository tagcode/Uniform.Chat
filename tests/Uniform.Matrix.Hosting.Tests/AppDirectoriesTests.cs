namespace Uniform.Matrix.Hosting.Tests;

/// <summary>Tests of <see cref="AppDirectories"/>: names and moving the folders of a renamed app.</summary>
public class AppDirectoriesTests
{
    /// <summary>Folders of the old name move to the new name with their files; a second run, or an existing new folder, moves nothing.</summary>
    [Fact]
    public void MoveFromPreviousName()
    {
        // Arrange: data and config of the old name, and a new config folder already in place
        string root = Directory.CreateTempSubdirectory("uniform-matrix-dirs-").FullName;
        string data = Path.Combine(root, "data"), config = Path.Combine(root, "config");
        Directory.CreateDirectory(Path.Combine(data, "old-app"));
        File.WriteAllText(Path.Combine(data, "old-app", "server.db"), "rows");
        Directory.CreateDirectory(Path.Combine(config, "old-app"));
        Directory.CreateDirectory(Path.Combine(config, "new-app"));

        // Act
        IReadOnlyList<string> moved = AppDirectories.MoveFromPreviousName("Old.App", "New.App", data, config);
        IReadOnlyList<string> again = AppDirectories.MoveFromPreviousName("Old.App", "New.App", data, config);

        // Assert: data moved, config kept apart, nothing the second time
        Assert.Single(moved);
        Assert.Equal("rows", File.ReadAllText(Path.Combine(data, "new-app", "server.db")));
        Assert.False(Directory.Exists(Path.Combine(data, "old-app")));
        Assert.True(Directory.Exists(Path.Combine(config, "old-app")));
        Assert.Empty(again);
        Directory.Delete(root, recursive: true);
    }
}
