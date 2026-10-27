namespace Uniform.Matrix.Client.Terminal.Ui;

using System.Collections.ObjectModel;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Core;
using global::Terminal.Gui.App;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>Modal dialogs of the client: first sign-in, room directory, reactions, devices and the image viewer; call on the UI thread. General dialogs are in <see cref="Dialogs"/>.</summary>
public static class ClientDialogs
{
    /// <summary>Let the user search the public room directory; return the alias or ID of the room to join, or null.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="search">Searches the directory for a term, or lists rooms when the term is empty; runs off the UI thread.</param>
    public static string? PickPublicRoom(IApplication app, Func<string?, Task<IReadOnlyList<PublicRoom>>> search)
    {
        // Build dialog: search field, results, buttons
        using Dialog dialog = new() { Title = "Room directory", Width = Dim.Percent(80), Height = Dim.Percent(80) };
        Label hint = new() { Text = "Search (Enter):", X = 1, Y = 0 };
        TextField field = new() { X = Pos.Right(hint) + 1, Y = 0, Width = Dim.Fill(1) };
        ObservableCollection<string> rows = ["Searching…"];
        List<PublicRoom> results = [];
        ListView list = new() { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        list.SetSource(rows);
        dialog.Add(hint, field, list);
        dialog.AddButton(new Button { Text = "_Close" });
        dialog.AddButton(new Button { Text = "_Join", IsDefault = true });

        // Search off the UI thread, then show results
        async void RunSearch(string? term)
        {
            rows.Clear();
            rows.Add("Searching…");
            try
            {
                IReadOnlyList<PublicRoom> found = await search(string.IsNullOrWhiteSpace(term) ? null : term.Trim());
                app.Invoke(() => ShowResults(found));
            }
            catch (Exception ex)
            {
                app.Invoke(() => { rows.Clear(); rows.Add($"Search failed: {ex.Message}"); });
            }
        }
        void ShowResults(IReadOnlyList<PublicRoom> found)
        {
            results.Clear();
            results.AddRange(found);
            rows.Clear();
            foreach (PublicRoom room in found) rows.Add($"{room.Name ?? room.CanonicalAlias ?? room.RoomId}  ({room.NumJoinedMembers})  {room.CanonicalAlias}  {room.Topic}".Trim());
            if (found.Count == 0) rows.Add("No rooms found");
        }

        // Enter in the field searches instead of closing
        field.Accepting += (_, e) => { RunSearch(field.Text); e.Handled = true; };
        RunSearch(null);
        app.Run(dialog);

        // Return the selected room when joining
        if (dialog.Result != 1 || list.SelectedItem is not int index || index >= results.Count) return null;
        return results[index].CanonicalAlias ?? results[index].RoomId;
    }

    /// <summary>Ask for the user ID and, when needed, the server address on first start; return them, or null when cancelled.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="userId">User ID to show at first, e.g. an invalid one from the command line.</param>
    /// <param name="homeserver">Server address to show at first.</param>
    public static (string UserId, string? Homeserver)? AskSignIn(IApplication app, string? userId, string? homeserver)
    {
        // Ask until the entries are valid or the dialog is closed; each round shows what was wrong
        string id = userId ?? "";
        string server = homeserver ?? "";
        string problem = "";
        while (true)
        {
            (bool accepted, id, server) = RunSignIn(app, id, server, problem);
            if (!accepted) return null;
            if (!UserId.TryParse(id, out _))
            {
                problem = id.Length == 0 ? "Give your user ID." : $"\"{id}\" is not a user ID; it looks like @alice:matrix.org.";
                continue;
            }
            if (server.Length > 0 && !(Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http"))
            {
                problem = $"\"{server}\" is not a server address; it looks like https://matrix.example.org.";
                continue;
            }
            // Return entries
            return (id, server.Length > 0 ? server : null);
        }
    }

    /// <summary>Show the sign-in dialog once; return whether it was accepted and the trimmed entries.</summary>
    private static (bool Accepted, string UserId, string Homeserver) RunSignIn(IApplication app, string userId, string homeserver, string problem)
    {
        // Fields with a line for problems; the password is asked for after this, when logging in
        using Dialog dialog = new() { Title = "Sign in to Matrix", Width = Dim.Percent(60) };
        Label intro = new() { Text = "Give your full Matrix user ID to start; it is remembered after a successful login.", X = 1, Y = 0, Width = Dim.Fill(1) };
        Label userLabel = new() { Text = "User ID, e.g. @alice:matrix.org:", X = 1, Y = Pos.Bottom(intro) + 1 };
        TextField userField = new() { Text = userId, X = 1, Y = Pos.Bottom(userLabel), Width = Dim.Fill(1) };
        Label serverLabel = new() { Text = "Server address, only when it cannot be found from the user ID:", X = 1, Y = Pos.Bottom(userField) + 1, Width = Dim.Fill(1) };
        TextField serverField = new() { Text = homeserver, X = 1, Y = Pos.Bottom(serverLabel), Width = Dim.Fill(1) };
        Label hint = new() { Text = "For example http://localhost:8008 for a server on this computer.", X = 1, Y = Pos.Bottom(serverField), Width = Dim.Fill(1) };
        Label problemLabel = new() { Text = problem, X = 1, Y = Pos.Bottom(hint) + 1, Width = Dim.Fill(1) };
        dialog.Add(intro, userLabel, userField, serverLabel, serverField, hint, problemLabel);
        dialog.AddButton(new Button { Text = "_Quit" });
        dialog.AddButton(new Button { Text = "_Sign in", IsDefault = true });

        // Run; Enter in a field accepts with the default button
        userField.SetFocus();
        app.Run(dialog);
        // Return entries
        return (dialog.Result == 1, userField.Text.Trim(), serverField.Text.Trim());
    }

    /// <summary>Ask for a reaction from common emoji or typed text; return null when cancelled.</summary>
    public static string? PickReaction(IApplication app)
    {
        // Common reactions, then free text
        string[] common = ["👍", "❤️", "😂", "🎉", "👀", "🙏"];
        int? choice = MessageBox.Query(app, "React", "Choose a reaction:", [.. common, "_Other…", "_Cancel"]);
        if (choice is not int index || index > common.Length) return null;
        // Return emoji or typed reaction
        return index < common.Length ? common[index] : Dialogs.Ask(app, "React", "Reaction text or emoji:");
    }

    /// <summary>Show devices; return the chosen action (1 rename, 2 log out device, 3 log out all others, 4 verify) and device, or null when closed.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="devices">Devices of the user.</param>
    /// <param name="ownDeviceId">This session's device, which cannot be logged out here.</param>
    /// <param name="trust">Trust label of a device ID, e.g. <c>verified</c>; null without encryption.</param>
    public static (int Action, Device? Device)? ChooseDeviceAction(IApplication app, IReadOnlyList<Device> devices, string? ownDeviceId, Func<string, string>? trust = null)
    {
        // List devices, this one first, then by last activity
        List<Device> ordered = [.. devices.OrderByDescending(d => d.DeviceId == ownDeviceId).ThenByDescending(d => d.LastSeenTs ?? 0)];
        ObservableCollection<string> rows = [.. ordered.Select(d => FormatDevice(d, ownDeviceId, trust?.Invoke(d.DeviceId)))];
        using Dialog dialog = new() { Title = $"Devices ({ordered.Count}, * = this device)", Width = Dim.Percent(80), Height = Dim.Percent(70) };
        ListView list = new() { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        list.SetSource(rows);
        dialog.Add(list);
        dialog.AddButton(new Button { Text = "_Close" });
        dialog.AddButton(new Button { Text = "_Rename" });
        dialog.AddButton(new Button { Text = "_Log out device" });
        dialog.AddButton(new Button { Text = "Log out _all others" });
        if (trust is not null) dialog.AddButton(new Button { Text = "_Verify" });

        // Run
        list.SetFocus();
        app.Run(dialog);
        if (dialog.Result is not int action || action == 0) return null;
        // Return action with the selected device
        Device? selected = list.SelectedItem is int index && index < ordered.Count ? ordered[index] : null;
        return (action, selected);
    }
    /// <summary>Format one device row: ID, trust, name, last activity and a mark for this device.</summary>
    private static string FormatDevice(Device device, string? ownDeviceId, string? trust)
    {
        // Last seen as local time
        string seen = device.LastSeenTs is long ts ? DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never";
        string own = device.DeviceId == ownDeviceId ? "*" : " ";
        // Return row; the mark is first so narrow dialogs keep it
        return trust is null
            ? $"{own} {device.DeviceId,-12} {device.DisplayName ?? "",-28} {seen}  {device.LastSeenIp}"
            : $"{own} {device.DeviceId,-12} {trust,-12} {device.DisplayName ?? "",-28} {seen}  {device.LastSeenIp}";
    }

    /// <summary>Show <paramref name="image"/> in a viewer: real pixels through Kitty or Sixel when the terminal supports them, colored cells otherwise.</summary>
    public static void ShowImage(IApplication app, string title, Images.DecodedImage image)
    {
        // Viewer filling most of the screen
        using Dialog dialog = new() { Title = $"{title} ({image.Width}×{image.Height})", Width = Dim.Percent(90), Height = Dim.Percent(90) };
        ImageView view = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), Image = image.ToColors(), UseRasterGraphics = true };
        dialog.Add(view);
        dialog.AddButton(new Button { Text = "_Close", IsDefault = true });
        // Run
        app.Run(dialog);
    }
}
