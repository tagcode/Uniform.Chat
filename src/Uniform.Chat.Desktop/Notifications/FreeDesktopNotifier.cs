namespace Uniform.Matrix.Client.Desktop.Notifications;

using Uniform.Matrix.Client.Avalonia.Core.Services;
using Tmds.DBus.Protocol;

/// <summary>Notifications on Linux desktops through the session bus, as GNOME, KDE and most others show them.</summary>
/// <remarks>Calls <see href="https://specifications.freedesktop.org/notification-spec/latest/protocol.html">org.freedesktop.Notifications.Notify</see>. A message in the same chat replaces the previous notification instead of stacking.</remarks>
public sealed class FreeDesktopNotifier : INotifier
{
    private const string DesktopEntry = "io.github.tagcode.uniform.matrix";
    private readonly Dictionary<string, uint> shownByTitle = [];
    private readonly string icon = File.Exists(Path.Combine(AppContext.BaseDirectory, "uniform-matrix.png")) ? Path.Combine(AppContext.BaseDirectory, "uniform-matrix.png") : DesktopEntry;

    /// <summary>Show a notification; fails when no notification service runs.</summary>
    public async Task ShowAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        // Notify(app_name, replaces_id, app_icon, summary, body, actions, hints, expire_timeout) -> id
        uint replaces;
        lock (shownByTitle) replaces = shownByTitle.GetValueOrDefault(title);
        MessageBuffer message = CreateNotify(replaces, title, body);
        uint id = await DBusConnection.Session.CallMethodAsync(message, (Message reply, object? _) => reply.GetBodyReader().ReadUInt32(), null).WaitAsync(cancellationToken);
        lock (shownByTitle) shownByTitle[title] = id;
    }

    /// <summary>Return the Notify call; the body is plain text, as markup support varies.</summary>
    private MessageBuffer CreateNotify(uint replaces, string title, string body)
    {
        // Header, then the arguments in signature order
        using MessageWriter writer = DBusConnection.Session.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.Notifications", path: "/org/freedesktop/Notifications", @interface: "org.freedesktop.Notifications", member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString("Uniform.Matrix");
        writer.WriteUInt32(replaces);
        writer.WriteString(icon);
        writer.WriteString(title);
        writer.WriteString(body.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"));
        writer.WriteArray(Array.Empty<string>());
        ArrayStart hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("desktop-entry");
        writer.WriteVariantString(DesktopEntry);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("category");
        writer.WriteVariantString("im.received");
        writer.WriteDictionaryEnd(hints);
        writer.WriteInt32(-1);
        // Return message
        return writer.CreateMessage();
    }
}
