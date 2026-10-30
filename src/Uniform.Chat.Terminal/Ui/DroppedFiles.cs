namespace Uniform.Chat.Terminal.Ui;

using System.Text;

/// <summary>Recognizes files dragged or pasted into the input line: terminals type them as paths, quoted, with escaped spaces, or as <c>file://</c> addresses.</summary>
public static class DroppedFiles
{
    /// <summary>Return the files in <paramref name="text"/> when it holds nothing but paths of existing files; an empty list when it is ordinary text.</summary>
    /// <param name="text">Text of the input line.</param>
    /// <param name="fileExists">Whether a path is an existing file; <see cref="File.Exists"/> outside tests.</param>
    public static IReadOnlyList<string> Parse(string text, Func<string, bool> fileExists)
    {
        // Every word must be a file; one that is not makes it a message
        List<string> files = [];
        foreach (string word in SplitWords(text))
        {
            string path = ToPath(word);
            if (path.Length == 0 || !fileExists(path)) return [];
            files.Add(path);
        }
        // Return files
        return files;
    }

    /// <summary>Split shell-style: single and double quotes group, a backslash escapes the next character, spaces separate.</summary>
    private static IEnumerable<string> SplitWords(string text)
    {
        // One pass, remembering the open quote
        StringBuilder word = new();
        char quote = '\0';
        bool inWord = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else word.Append(c);
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
                inWord = true;
                continue;
            }
            if (c == '\\' && i + 1 < text.Length && !OperatingSystem.IsWindows())
            {
                word.Append(text[++i]);
                inWord = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inWord) yield return word.ToString();
                word.Clear();
                inWord = false;
                continue;
            }
            word.Append(c);
            inWord = true;
        }
        // Done
        if (inWord) yield return word.ToString();
    }

    /// <summary>Return a local path from a word: <c>file://</c> addresses decoded, <c>~/</c> expanded; empty for words that are not absolute paths.</summary>
    private static string ToPath(string word)
    {
        // Addresses of local files
        if (word.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return Uri.TryCreate(word, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : "";
        if (word.StartsWith("~/", StringComparison.Ordinal)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), word[2..]);
        // Return absolute paths only, so ordinary words are never taken for files
        return Path.IsPathFullyQualified(word) ? word : "";
    }
}
