namespace Uniform.Chat.Terminal.Ui;

using System.Collections.ObjectModel;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using global::Terminal.Gui.App;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;
using Uniform.Chat.Terminal.Localization;

/// <summary>Modal dialogs of the client: first sign-in and creating an account, room directory, reactions, devices and the image viewer; call on the UI thread. General dialogs are in <see cref="Dialogs"/>.</summary>
public static class ClientDialogs
{
    /// <summary>Let the user search the public room directory of their server or another one; return the room to join with the server it was listed on, or null.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="server">Server to show at first; empty for the user's own.</param>
    /// <param name="search">Searches the directory of a server, null for the user's own, for a term, or lists rooms when the term is null; runs off the UI thread.</param>
    public static (MatrixLink Room, string Server)? PickPublicRoom(IApplication app, string server, Func<string?, string?, Task<IReadOnlyList<PublicRoom>>> search)
    {
        // Build dialog: server and search fields, results, buttons
        using Dialog dialog = new() { Title = Strings.RoomDirectory, Width = Dim.Percent(80), Height = Dim.Percent(80) };
        Label serverHint = new() { Text = Strings.ServerLabel, X = 1, Y = 0 };
        TextField serverField = new() { Text = server, X = Pos.Right(serverHint) + 1, Y = 0, Width = Dim.Percent(35) };
        Label hint = new() { Text = Strings.SearchLabel, X = Pos.Right(serverField) + 2, Y = 0 };
        TextField field = new() { X = Pos.Right(hint) + 1, Y = 0, Width = Dim.Fill(1) };
        Label note = new() { Text = Strings.DirectoryHint, X = 1, Y = 1, Width = Dim.Fill(1) };
        ObservableCollection<string> rows = [Strings.Searching];
        List<PublicRoom> results = [];
        string shownServer = "";
        ListView list = new() { X = 1, Y = 3, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        list.SetSource(rows);
        dialog.Add(serverHint, serverField, hint, field, note, list);
        dialog.AddButton(new Button { Text = Strings.ButtonClose });
        dialog.AddButton(new Button { Text = Strings.ButtonJoin, IsDefault = true });

        // Search off the UI thread, then show results; a late answer for another server is dropped
        async void RunSearch()
        {
            string other = serverField.Text.Trim();
            string term = field.Text.Trim();
            shownServer = other;
            results.Clear();
            rows.Clear();
            rows.Add(other.Length > 0 ? Texts.Format(Strings.Asking, other) : Strings.Searching);
            try
            {
                IReadOnlyList<PublicRoom> found = await search(other.Length > 0 ? other : null, term.Length > 0 ? term : null);
                app.Invoke(() => { if (shownServer == other) ShowResults(found); });
            }
            catch (Exception ex)
            {
                app.Invoke(() => { if (shownServer == other) { rows.Clear(); rows.Add(Texts.Format(Strings.SearchFailed, ex.Message)); } });
            }
        }
        void ShowResults(IReadOnlyList<PublicRoom> found)
        {
            results.Clear();
            results.AddRange(found);
            rows.Clear();
            foreach (PublicRoom room in found) rows.Add($"{room.Name ?? room.CanonicalAlias ?? room.RoomId}  ({room.NumJoinedMembers})  {room.CanonicalAlias}  {room.Topic}".Trim());
            if (found.Count == 0) rows.Add(Strings.NoRoomsFound);
        }

        // Enter in a field searches instead of closing
        field.Accepting += (_, e) => { RunSearch(); e.Handled = true; };
        serverField.Accepting += (_, e) => { RunSearch(); e.Handled = true; };
        RunSearch();
        app.Run(dialog);

        // Return the selected room, joined through the server that listed it
        if (dialog.Result != 1 || list.SelectedItem is not int index || index >= results.Count) return null;
        PublicRoom chosen = results[index];
        return (new MatrixLink { Target = chosen.CanonicalAlias ?? chosen.RoomId, Via = shownServer.Length > 0 ? [shownServer] : [] }, shownServer);
    }

    /// <summary>Address of the app's user policy, which people accept by signing in.</summary>
    public const string UserPolicyUrl = "https://github.com/tagcode/Uniform.Chat/blob/main/user-docs/Uniform.Chat.Android/user-policy.md";

    /// <summary>Ask for the user ID and, when needed, the server address on first start, and whether to create the account; return them, or null when cancelled.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="userId">User ID to show at first, e.g. an invalid one from the command line.</param>
    /// <param name="homeserver">Server address to show at first.</param>
    public static (string UserId, string? Homeserver, bool CreateAccount)? AskSignIn(IApplication app, string? userId, string? homeserver)
    {
        // Ask until the entries are valid or the dialog is closed; each round shows what was wrong
        string id = userId ?? "";
        string server = homeserver ?? "";
        string problem = "";
        while (true)
        {
            (int? button, id, server) = RunSignIn(app, id, server, problem);
            if (button is not (1 or 2)) return null;
            if (!UserId.TryParse(id, out _))
            {
                problem = id.Length == 0 ? Strings.GiveYourUserId : Texts.Format(Strings.NotAUserIdExample, id);
                continue;
            }
            if (server.Length > 0 && !(Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http"))
            {
                problem = Texts.Format(Strings.NotAServerAddress, server);
                continue;
            }
            // Return entries
            return (id, server.Length > 0 ? server : null, button == 1);
        }
    }

    /// <summary>Show the sign-in dialog once; return the button pressed (1 create account, 2 sign in), or null when closed, and the trimmed entries.</summary>
    private static (int? Button, string UserId, string Homeserver) RunSignIn(IApplication app, string userId, string homeserver, string problem)
    {
        // Fields with a line for problems; the password is asked for after this, when logging in
        using Dialog dialog = new() { Title = Strings.SignInToMatrix, Width = Dim.Percent(60) };
        Label intro = new() { Text = Strings.SignInIntro, X = 1, Y = 0, Width = Dim.Fill(1) };
        Label userLabel = new() { Text = Strings.UserIdPrompt, X = 1, Y = Pos.Bottom(intro) + 1 };
        TextField userField = new() { Text = userId, X = 1, Y = Pos.Bottom(userLabel), Width = Dim.Fill(1) };
        Label serverLabel = new() { Text = Strings.ServerAddressPrompt, X = 1, Y = Pos.Bottom(userField) + 1, Width = Dim.Fill(1) };
        TextField serverField = new() { Text = homeserver, X = 1, Y = Pos.Bottom(serverLabel), Width = Dim.Fill(1) };
        Label hint = new() { Text = Strings.ServerAddressHint, X = 1, Y = Pos.Bottom(serverField), Width = Dim.Fill(1) };
        Label policy = new() { Text = Texts.Format(Strings.AcceptPolicy, UserPolicyUrl), X = 1, Y = Pos.Bottom(hint) + 1, Width = Dim.Fill(1) };
        Label problemLabel = new() { Text = problem, X = 1, Y = Pos.Bottom(policy) + 1, Width = Dim.Fill(1) };
        dialog.Add(intro, userLabel, userField, serverLabel, serverField, hint, policy, problemLabel);
        dialog.AddButton(new Button { Text = Strings.ButtonQuit });
        dialog.AddButton(new Button { Text = Strings.ButtonCreateAccount });
        dialog.AddButton(new Button { Text = Strings.ButtonSignIn, IsDefault = true });

        // Run; Enter in a field accepts with the default button
        userField.SetFocus();
        app.Run(dialog);
        // Return entries
        return (dialog.Result, userField.Text.Trim(), serverField.Text.Trim());
    }

    /// <summary>Create the account of <paramref name="userId"/>: ask for a password twice, then the registration token and the server's policies when it has them; return the password to sign in with, or null when cancelled.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="userId">User ID to create.</param>
    /// <param name="homeserver">Server address, or null to find it from the user ID.</param>
    public static string? CreateAccount(IApplication app, string userId, string? homeserver)
    {
        // Password twice
        if (Dialogs.Ask(app, Strings.CreateAccount, Texts.Format(Strings.APasswordFor, userId), secret: true) is not { Length: > 0 } password) return null;
        if (Dialogs.Ask(app, Strings.CreateAccount, Strings.SamePasswordAgain, secret: true) != password)
        {
            Dialogs.Error(app, Strings.CreateAccount, Strings.PasswordsDiffer);
            return null;
        }

        // What the server needs; network calls run off the UI thread, whose context is busy waiting
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        UserId id = UserId.Parse(userId);
        Uri server;
        RegistrationRequirements requirements;
        try
        {
            (server, requirements) = Task.Run(async () =>
            {
                Uri found = await AccountRegistration.FindHomeserverAsync(http, id, homeserver);
                return (found, await AccountRegistration.GetRequirementsAsync(http, found));
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException or TaskCanceledException)
        {
            Dialogs.Error(app, Strings.CreateAccount, ex is MatrixException { Errcode: ErrorCodes.Forbidden } ? Strings.NoAppRegistration : ex.Message);
            return null;
        }
        if (!requirements.Supported)
        {
            Dialogs.Error(app, Strings.CreateAccount, Strings.RegistrationStepUnsupported);
            return null;
        }

        // Token and policies when asked for
        string? token = requirements.NeedsToken ? Dialogs.Ask(app, Strings.CreateAccount, Strings.RegistrationTokenPrompt)?.Trim() : null;
        if (requirements.NeedsToken && string.IsNullOrEmpty(token)) return null;
        bool accepted = requirements.Policies.Count == 0 || Dialogs.Choose(app, Strings.CreateAccount, Texts.Format(Strings.ServerPolicies, string.Join("\n", requirements.Policies.Select(p => $"{p.Name}: {p.Url}"))), [Strings.ButtonCancel, Strings.ButtonAcceptThem]) == 1;
        if (!accepted) return null;

        // Register; the login that follows uses the same password
        try
        {
            Task.Run(() => AccountRegistration.RegisterAsync(http, server, id, password, token, requirements.Policies.Count > 0)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException or TaskCanceledException or UserInteractiveAuthException)
        {
            Dialogs.Error(app, Strings.CreateAccount, ex switch
            {
                MatrixException { Errcode: ErrorCodes.UserInUse } => Texts.Format(Strings.UserIdTaken, userId),
                UserInteractiveAuthException => Strings.TokenNotAccepted,
                _ => ex.Message,
            });
            return null;
        }
        // Return password
        return password;
    }

    /// <summary>Ask for a reaction from common emoji or typed text; return null when cancelled.</summary>
    public static string? PickReaction(IApplication app)
    {
        // Common reactions, then free text
        string[] common = ["👍", "❤️", "😂", "🎉", "👀", "🙏"];
        int? choice = MessageBox.Query(app, Strings.React, Strings.ChooseReaction, [.. common, Strings.ButtonOther, Strings.ButtonCancel]);
        if (choice is not int index || index > common.Length) return null;
        // Return emoji or typed reaction
        return index < common.Length ? common[index] : Dialogs.Ask(app, Strings.React, Strings.ReactionPrompt);
    }

    /// <summary>Show devices; return the chosen action (1 rename, 2 log out device, 3 log out all others, 4 verify) and device, or null when closed.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="devices">Devices of the user.</param>
    /// <param name="ownDeviceId">This session's device, which cannot be logged out here.</param>
    /// <param name="trust">Trust label of a device ID, e.g. <c>verified</c>, or <see cref="WarningListSource.NotTrusted"/>, which shows on red; null without encryption.</param>
    public static (int Action, Device? Device)? ChooseDeviceAction(IApplication app, IReadOnlyList<Device> devices, string? ownDeviceId, Func<string, string>? trust = null)
    {
        // List devices, this one first, then by last activity
        List<Device> ordered = [.. devices.OrderByDescending(d => d.DeviceId == ownDeviceId).ThenByDescending(d => d.LastSeenTs ?? 0)];
        ObservableCollection<string> rows = [.. ordered.Select(d => FormatDevice(d, ownDeviceId, trust?.Invoke(d.DeviceId)))];
        using Dialog dialog = new() { Title = Texts.Format(Strings.DevicesTitle, ordered.Count), Width = Dim.Percent(80), Height = Dim.Percent(70) };
        ListView list = new() { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        list.Source = new WarningListSource(rows, WarningListSource.NotTrusted);
        dialog.Add(list);
        dialog.AddButton(new Button { Text = Strings.ButtonClose });
        dialog.AddButton(new Button { Text = Strings.ButtonRename });
        dialog.AddButton(new Button { Text = Strings.ButtonLogOutDevice });
        dialog.AddButton(new Button { Text = Strings.ButtonLogOutOthers });
        if (trust is not null) dialog.AddButton(new Button { Text = Strings.ButtonVerifySelected });

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
        string seen = device.LastSeenTs is long ts ? DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Strings.Never;
        string own = device.DeviceId == ownDeviceId ? "*" : " ";
        // Return row; the mark is first so narrow dialogs keep it
        return trust is null
            ? $"{own} {device.DeviceId,-12} {device.DisplayName ?? "",-28} {seen}  {device.LastSeenIp}"
            : $"{own} {device.DeviceId,-12} {trust,-13} {device.DisplayName ?? "",-28} {seen}  {device.LastSeenIp}";
    }

    /// <summary>Show <paramref name="image"/> in a viewer: real pixels through Kitty or Sixel when the terminal supports them, colored cells otherwise.</summary>
    public static void ShowImage(IApplication app, string title, Images.DecodedImage image)
    {
        // Viewer filling most of the screen
        using Dialog dialog = new() { Title = $"{title} ({image.Width}×{image.Height})", Width = Dim.Percent(90), Height = Dim.Percent(90) };
        ImageView view = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), Image = image.ToColors(), UseRasterGraphics = true };
        dialog.Add(view);
        dialog.AddButton(new Button { Text = Strings.ButtonClose, IsDefault = true });
        // Run
        app.Run(dialog);
    }
}
