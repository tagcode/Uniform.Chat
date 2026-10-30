namespace Uniform.Chat.Terminal.Tests;

using Uniform.Chat.Terminal.Ui;

/// <summary>Tests of <see cref="DroppedFiles"/>: files typed into the input line by drag and drop or paste.</summary>
public class DroppedFilesTests
{
    private static readonly HashSet<string> Existing = ["/home/alice/cat photo.png", "/home/alice/notes.txt", "/tmp/a'b.txt"];

    /// <summary>Return the files <see cref="DroppedFiles"/> finds when only <see cref="Existing"/> exist.</summary>
    private static IReadOnlyList<string> Parse(string text) => DroppedFiles.Parse(text, Existing.Contains);

    /// <summary>Paths as terminals type them: quoted, with escaped spaces, as file addresses, several at once.</summary>
    [Fact]
    public void FindFiles()
    {
        // Skip on Windows, where backslashes are path separators
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix paths");
        Assert.Equal(["/home/alice/cat photo.png"], Parse("'/home/alice/cat photo.png' "));
        Assert.Equal(["/home/alice/cat photo.png"], Parse(@"/home/alice/cat\ photo.png"));
        Assert.Equal(["/home/alice/cat photo.png"], Parse("file:///home/alice/cat%20photo.png"));
        Assert.Equal(["/tmp/a'b.txt"], Parse("\"/tmp/a'b.txt\""));
        Assert.Equal(["/home/alice/notes.txt", "/home/alice/cat photo.png"], Parse("/home/alice/notes.txt '/home/alice/cat photo.png'"));
    }

    /// <summary>Messages stay messages: words, commands, missing files, and a file mentioned in a sentence.</summary>
    [Fact]
    public void KeepMessages()
    {
        // Assert
        Assert.Empty(Parse("hello there"));
        Assert.Empty(Parse("/me waves"));
        Assert.Empty(Parse("/home/alice/missing.txt"));
        Assert.Empty(Parse("see /home/alice/notes.txt"));
        Assert.Empty(Parse("notes.txt"));
        Assert.Empty(Parse(""));
    }
}
