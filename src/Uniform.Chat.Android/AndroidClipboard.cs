namespace Uniform.Chat.Android;

using global::Android.Content;
using global::Android.Database;
using global::Android.Provider;
using Uniform.Chat.Avalonia;

/// <summary>Reads what Android's clipboard holds as a link to content, e.g. a copied screenshot or an image copied in a browser, which Avalonia's clipboard, of text, does not read.</summary>
internal static class AndroidClipboard
{
    /// <summary>Return the first item on the clipboard that is content rather than text, opened, with its name; null when there is none or it cannot be read.</summary>
    public static ClipboardFile? ReadFile(Context context)
    {
        // The clipboard's first content link
        if (context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard || clipboard.PrimaryClip is not ClipData clip) return null;
        for (int i = 0; i < clip.ItemCount; i++)
        {
            if (clip.GetItemAt(i)?.Uri is not global::Android.Net.Uri uri) continue;
            try
            {
                // Its bytes and its name
                ContentResolver resolver = context.ContentResolver!;
                string type = resolver.GetType(uri) ?? "application/octet-stream";
                if (resolver.OpenInputStream(uri) is not Stream content) continue;
                return new ClipboardFile(content, NameOf(resolver, uri, type));
            }
            catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.IO.FileNotFoundException or Java.Lang.IllegalArgumentException)
            {
                // On content the app may not read
                global::Android.Util.Log.Warn("UniformChat", $"Clipboard content not readable: {ex.Message}");
            }
        }
        // On text only
        return null;
    }

    /// <summary>Return the content's display name, else a name from its type, e.g. <c>image-20261111-191500.png</c>.</summary>
    private static string NameOf(ContentResolver resolver, global::Android.Net.Uri uri, string type)
    {
        // The provider's name, when it tells
        try
        {
            using ICursor? cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (cursor is not null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } name) return name;
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            // On a provider that does not tell
        }
        // Return a name from the type
        string extension = global::Android.Webkit.MimeTypeMap.Singleton?.GetExtensionFromMimeType(type) ?? "bin";
        return $"{(type.StartsWith("image/", StringComparison.Ordinal) ? "image" : "file")}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}";
    }
}
