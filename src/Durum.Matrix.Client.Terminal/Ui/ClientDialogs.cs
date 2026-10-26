namespace Durum.Matrix.Client.Terminal.Ui;

using System.Collections.ObjectModel;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Core;
using global::Terminal.Gui.App;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>Modal dialogs of the client: room directory, reactions, devices and the image viewer; call on the UI thread. General dialogs are in <see cref="Dialogs"/>.</summary>
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
