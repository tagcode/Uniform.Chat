namespace Uniform.Chat.Android;

using global::Android.App;
using global::Android.Content;
using global::Android.OS;
using Uniform.Chat.Avalonia.Services;

/// <summary>Puts the app in Android's share sheet: takes the shared text or files, copies the files while the app may read them, hands them to <see cref="ShareInbox"/>, opens the app to choose a room, and closes.</summary>
[Activity(
    Label = "Uniform Chat",
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    Exported = true,
    NoHistory = true,
    ExcludeFromRecents = true)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "*/*")]
public sealed class ShareActivity : Activity
{
    /// <summary>Take the share, off the UI thread as files may be large, then open the app.</summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Read what was shared while this activity holds the grants
        base.OnCreate(savedInstanceState);
        Intent? intent = Intent;
        _ = Task.Run(() =>
        {
            SharedContent? content = intent is null ? null : Read(intent);
            RunOnUiThread(() =>
            {
                // Nothing usable: back to the sharing app
                if (content is null) { Finish(); return; }
                ShareInbox.Put(content);
                // Done: the app on top, its open activity reused
                StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop));
                Finish();
            });
        });
    }

    /// <summary>Return the text and copies of the files of a send intent; null when it has neither.</summary>
    private SharedContent? Read(Intent intent)
    {
        // Text, e.g. a link with the page's title as subject
        string? text = intent.GetStringExtra(Intent.ExtraText);
        List<global::Android.Net.Uri> uris = [];
        if (intent.Action == Intent.ActionSendMultiple)
        {
#pragma warning disable CA1422 // The typed overload is Android 13 and later
            System.Collections.IList? parcels = intent.GetParcelableArrayListExtra(Intent.ExtraStream);
#pragma warning restore CA1422
            foreach (object? parcel in parcels ?? Array.Empty<object>())
            {
                if (parcel is global::Android.Net.Uri uri) uris.Add(uri);
            }
        }
#pragma warning disable CA1422 // The typed overload is Android 13 and later
        else if (intent.GetParcelableExtra(Intent.ExtraStream) is global::Android.Net.Uri uri) uris.Add(uri);
#pragma warning restore CA1422
        // Each file copied into the cache
        List<SharedFile> files = [];
        foreach (global::Android.Net.Uri uri in uris)
        {
            if (Copy(uri) is SharedFile file) files.Add(file);
        }
        if (string.IsNullOrWhiteSpace(text) && files.Count == 0) return null;
        // Return content
        return new SharedContent(string.IsNullOrWhiteSpace(text) ? null : text, files);
    }

    /// <summary>Copy shared content into the app's cache, with its name; null when it cannot be read.</summary>
    private SharedFile? Copy(global::Android.Net.Uri uri)
    {
        try
        {
            // Name and bytes
            ContentResolver resolver = ContentResolver!;
            string name = AndroidClipboard.NameOf(resolver, uri, resolver.GetType(uri) ?? "application/octet-stream");
            using Stream? source = resolver.OpenInputStream(uri);
            if (source is null) return null;
            string directory = Path.Combine(CacheDir!.AbsolutePath, "shared");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(name)}");
            using (FileStream target = File.Create(path)) source.CopyTo(target);
            // Return copy
            return new SharedFile(path, name);
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.IO.FileNotFoundException or Java.Lang.IllegalArgumentException or IOException)
        {
            // On content the app may not read
            global::Android.Util.Log.Warn("UniformChat", $"Shared content not readable: {ex.Message}");
            return null;
        }
    }
}
