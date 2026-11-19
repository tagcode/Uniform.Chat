namespace Uniform.Chat.Terminal.Ui;

using Uniform.Matrix.Client;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Chat.Terminal.Localization;

// Room administration: name, avatar, public rooms, aliases, directory listing, kick, ban, unban and power levels.
public sealed partial class MainWindow
{
    /// <summary>Set the name of the open room, asking with the current name when <paramref name="name"/> is empty.</summary>
    private void SetRoomName(string name)
    {
        // Assert room and ask
        if (currentRoomId is not string roomId) return;
        string current = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.Name ?? "" : "");
        string? text = name.Length > 0 ? name : Dialogs.Ask(app, Strings.SetName, Strings.RoomNamePrompt, current);
        if (text is null) return;
        // Set
        _ = RunSafelyAsync(Strings.SettingName, client => client.SetRoomNameAsync(roomId, text));
    }

    /// <summary>Set the name from the menu.</summary>
    private void SetRoomName() => SetRoomName("");

    /// <summary>Create a public room with alias <paramref name="alias"/> on the user's server, listed in its directory, asking for what is not given.</summary>
    /// <param name="alias">Alias like <c>#room</c> or <c>#room:server</c>; asked for when empty.</param>
    /// <param name="roomName">Room name; the alias's name when empty.</param>
    private void CreatePublicRoom(string alias, string roomName)
    {
        // Ask for the alias, then check it is on this server
        string? text = alias.Length > 0 ? alias : Dialogs.Ask(app, Strings.NewPublicRoom, Texts.Format(Strings.AddressPrompt, OwnServerName));
        if (text is null) return;
        if (LocalAliasName(text) is not string localpart) return;
        string name = roomName.Length > 0 ? roomName : localpart;

        // Create unencrypted, as anyone may join and read; listed in the directory
        _ = RunSafelyAsync(Texts.Format(Strings.CreatingAlias, localpart, OwnServerName), async client =>
        {
            await client.CreateRoomAsync(new CreateRoomRequest { Name = name, Preset = RoomPresets.PublicChat, RoomAliasName = localpart, Visibility = "public" });
            app.Invoke(() => ShowStatus(Texts.Format(Strings.CreatedAlias, localpart, OwnServerName)));
        });
    }

    /// <summary>Create a public room from the menu.</summary>
    private void CreatePublicRoom() => CreatePublicRoom("", "");

    /// <summary>Add alias <paramref name="alias"/> on the user's server to the open room, asking when empty; it becomes the main alias when the room has none.</summary>
    private void AddAlias(string alias)
    {
        // Assert room, ask and check the alias
        if (currentRoomId is not string roomId) return;
        string? text = alias.Length > 0 ? alias : Dialogs.Ask(app, Strings.AddAlias, Texts.Format(Strings.AddressPrompt, OwnServerName));
        if (text is null || LocalAliasName(text) is not string localpart) return;
        string full = $"#{localpart}:{OwnServerName}";
        bool hasMain = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.CanonicalAlias is not null);

        // Create, and make it the main alias that clients show
        _ = RunSafelyAsync(Texts.Format(Strings.Adding, full), async client =>
        {
            await client.CreateRoomAliasAsync(full, roomId);
            if (!hasMain) await client.SendStateEventAsync(roomId, EventTypes.RoomCanonicalAlias, new CanonicalAliasContent { Alias = full });
            app.Invoke(() => ShowStatus(Texts.Format(Strings.Added, full)));
        });
    }

    /// <summary>Add an alias from the menu.</summary>
    private void AddAlias() => AddAlias("");

    /// <summary>Remove alias <paramref name="alias"/> of the user's server, asking when empty.</summary>
    private void RemoveAlias(string alias)
    {
        // Ask and check the alias
        string? text = alias.Length > 0 ? alias : Dialogs.Ask(app, Strings.RemoveAlias, Texts.Format(Strings.AliasRemovePrompt, OwnServerName));
        if (text is null || LocalAliasName(text) is not string localpart) return;
        string full = $"#{localpart}:{OwnServerName}";
        // Remove
        _ = RunSafelyAsync(Texts.Format(Strings.Removing, full), async client => { await client.DeleteRoomAliasAsync(full); app.Invoke(() => ShowStatus(Texts.Format(Strings.Removed, full))); });
    }

    /// <summary>Ask whether to list the open room in the public room directory of the user's server.</summary>
    private void ChangeDirectoryListing()
    {
        // Assert room and ask
        if (currentRoomId is not string roomId) return;
        string name = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.DisplayName : roomId);
        int? choice = Dialogs.Choose(app, Strings.DirectoryListing, Texts.Format(Strings.ListQuestion, name, OwnServerName), [Strings.ButtonList, Strings.ButtonUnlist, Strings.ButtonClose]);
        // List or unlist
        if (choice is 0 or 1) SetDirectoryListing(listed: choice == 0);
    }

    /// <summary>List the open room in the public room directory of the user's server, or remove it.</summary>
    private void SetDirectoryListing(bool listed)
    {
        // Assert room
        if (currentRoomId is not string roomId) return;
        string name = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.DisplayName : roomId);
        // Set
        _ = RunSafelyAsync(listed ? Texts.Format(Strings.ListingRoom, name) : Texts.Format(Strings.UnlistingRoom, name), async client =>
        {
            await client.SetRoomDirectoryVisibilityAsync(roomId, listed);
            app.Invoke(() => ShowStatus(listed ? Texts.Format(Strings.RoomListed, name) : Texts.Format(Strings.RoomUnlisted, name)));
        });
    }

    /// <summary>Server name of the user, the only server where they can make aliases.</summary>
    private string OwnServerName => UserId.TryParse(ownUserId, out UserId user) ? user.ServerName : "";

    /// <summary>Return the name part of an alias like <c>#room</c> or <c>#room:server</c> of the user's server, or show why it is not one and return null.</summary>
    private string? LocalAliasName(string alias)
    {
        // Sigil, name, and this server when one is given
        string text = alias.Trim();
        int colon = text.IndexOf(':');
        string localpart = colon < 0 ? text.TrimStart('#') : text[..colon].TrimStart('#');
        string? server = colon < 0 ? null : text[(colon + 1)..];
        if (!text.StartsWith('#') || localpart.Length == 0 || localpart.Any(c => char.IsWhiteSpace(c) || c == ':'))
        {
            Dialogs.Error(app, Strings.InvalidAddress, Texts.Format(Strings.NotARoomAddress, alias, OwnServerName));
            return null;
        }
        if (server is not null && server != OwnServerName)
        {
            Dialogs.Error(app, Strings.OtherServer, Texts.Format(Strings.OwnServerOnly, OwnServerName));
            return null;
        }
        // Return name part
        return localpart;
    }

    /// <summary>Set the avatar of the open room from an image file, picking one when <paramref name="argument"/> is empty; <c>none</c> removes it.</summary>
    private void SetRoomAvatar(string argument)
    {
        // Assert room; removing needs no file
        if (currentRoomId is not string roomId) return;
        if (argument.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunSafelyAsync(Strings.RemovingAvatar, client => client.SetRoomAvatarAsync(roomId, null));
            return;
        }

        // An image file, given or picked
        string? path = argument.Length > 0 ? ExpandHome(argument) : Dialogs.PickFile(app, Strings.RoomAvatar);
        if (path is null) return;
        string mimeType = MimeTypes.FromPath(path);
        if (!File.Exists(path) || !mimeType.StartsWith("image/", StringComparison.Ordinal))
        {
            Dialogs.Error(app, Strings.RoomAvatar, Texts.Format(Strings.NotAnImage, path));
            return;
        }

        // Upload, then point the room at it; avatars are room state, so they are not encrypted
        string name = Path.GetFileName(path);
        ShowStatus(Texts.Format(Strings.Uploading, name));
        _ = RunSafelyAsync(Strings.SettingAvatar, async client =>
        {
            await using FileStream content = File.OpenRead(path);
            MxcUri uri = await client.UploadMediaAsync(content, mimeType, name);
            await client.SetRoomAvatarAsync(roomId, uri);
            app.Invoke(() => ShowStatus(Strings.RoomAvatarSet));
        });
    }

    /// <summary>Set the avatar from the menu.</summary>
    private void SetRoomAvatar() => SetRoomAvatar("");

    /// <summary>Remove a member from the open room; they may join again. <paramref name="argument"/> is <c>user [reason]</c>; asks when empty.</summary>
    private void KickMember(string argument) => ChangeMembership(argument, Strings.Kick, Strings.Kicking, (client, roomId, userId, reason) => client.KickAsync(roomId, userId, reason));

    /// <summary>Kick from the menu.</summary>
    private void KickMember() => KickMember("");

    /// <summary>Remove a member from the open room and stop them joining again. <paramref name="argument"/> is <c>user [reason]</c>; asks when empty.</summary>
    private void BanMember(string argument) => ChangeMembership(argument, Strings.Ban, Strings.Banning, (client, roomId, userId, reason) => client.BanAsync(roomId, userId, reason));

    /// <summary>Ban from the menu.</summary>
    private void BanMember() => BanMember("");

    /// <summary>Let a banned user join the open room again. <paramref name="argument"/> is <c>user [reason]</c>; asks when empty.</summary>
    private void UnbanMember(string argument) => ChangeMembership(argument, Strings.Unban, Strings.Unbanning, (client, roomId, userId, reason) => client.UnbanAsync(roomId, userId, reason));

    /// <summary>Unban from the menu.</summary>
    private void UnbanMember() => UnbanMember("");

    /// <summary>Kick, ban or unban a user of the open room: from <paramref name="argument"/> (<c>user [reason]</c>), or by asking for the user and an optional reason.</summary>
    private void ChangeMembership(string argument, string title, string doing, Func<MatrixClient, string, string, string?, Task> change)
    {
        // Assert room; the user from the argument or asked
        if (currentRoomId is not string roomId) return;
        string[] parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? target = parts.Length > 0 ? parts[0] : Dialogs.Ask(app, title, Strings.MemberPrompt);
        if (target is null) return;
        if (ResolveMember(roomId, target) is not string userId)
        {
            Dialogs.Error(app, title, Texts.Format(Strings.NoMemberHere, target));
            return;
        }

        // Reason from the argument, or asked when the user was asked for too
        string? reason = parts.Length > 1 ? parts[1] : parts.Length == 0 ? Dialogs.AskAllowEmpty(app, title, Texts.Format(Strings.ReasonFor, userId)) : "";
        if (reason is null) return;
        // Done
        _ = RunSafelyAsync($"{doing} {userId}", client => change(client, roomId, userId, reason.Length > 0 ? reason : null));
    }

    /// <summary>Set a member's power level in the open room. <paramref name="argument"/> is <c>user [level]</c>, where level is a number or admin, moderator or member; asks for what is missing.</summary>
    private void SetPowerLevel(string argument)
    {
        // Assert room; the user from the argument or asked
        if (currentRoomId is not string roomId) return;
        string[] parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? target = parts.Length > 0 ? parts[0] : Dialogs.Ask(app, Strings.PowerLevel, Strings.MemberPrompt);
        if (target is null) return;
        if (ResolveMember(roomId, target) is not string userId)
        {
            Dialogs.Error(app, Strings.PowerLevel, Texts.Format(Strings.NoMemberHere, target));
            return;
        }

        // Creators of newer rooms have no level to change
        if (processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.Creators.Contains(userId)))
        {
            Dialogs.Error(app, Strings.PowerLevel, Texts.Format(Strings.CreatorUnlimited, userId));
            return;
        }

        // Level from the argument, or picked with the current one marked
        (long current, long own) = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? (room.GetPowerLevel(userId), room.GetPowerLevel(ownUserId)) : (0L, 0L));
        long? level = parts.Length > 1 ? ParseLevel(parts[1]) : PickLevel(userId, current);
        if (level is not long newLevel)
        {
            if (parts.Length > 1) Dialogs.Error(app, Strings.PowerLevel, Texts.Format(Strings.NotALevel, parts[1]));
            return;
        }

        // Lowering your own level cannot be undone by yourself
        if (userId == ownUserId && newLevel < own && !Dialogs.Confirm(app, Strings.PowerLevel, Texts.Format(Strings.LowerOwnLevel, own, newLevel))) return;
        // Done
        _ = RunSafelyAsync(Texts.Format(Strings.SettingPowerLevel, userId), client => client.SetUserPowerLevelAsync(roomId, userId, newLevel));
    }

    /// <summary>Set a power level from the menu.</summary>
    private void SetPowerLevel() => SetPowerLevel("");

    /// <summary>Let the user pick a level for <paramref name="userId"/>: admin, moderator, member or a number; return null when cancelled.</summary>
    private long? PickLevel(string userId, long current)
    {
        // Usual roles, then a number
        string[] options = [Texts.Format(Strings.LevelAdmin, PowerLevelsContent.Admin), Texts.Format(Strings.LevelModerator, PowerLevelsContent.Moderator), Strings.LevelMember, Strings.LevelOther];
        string? currentOption = current switch
        {
            PowerLevelsContent.Admin => options[0],
            PowerLevelsContent.Moderator => options[1],
            0 => options[2],
            _ => null,
        };
        string? choice = Dialogs.PickOne(app, Texts.Format(Strings.PowerLevelOf, userId, current), options, currentOption);
        if (choice is null) return null;
        if (choice != options[3]) return ParseLevel(choice.Split(' ')[0]);
        // Return typed number
        return Dialogs.Ask(app, Strings.PowerLevel, Texts.Format(Strings.LevelFor, userId), current.ToString()) is string typed ? ParseLevel(typed) : null;
    }

    /// <summary>Return a level from a number or a role name; null for anything else.</summary>
    private static long? ParseLevel(string text) => text.Trim().ToLowerInvariant() switch
    {
        "admin" => PowerLevelsContent.Admin,
        "moderator" or "mod" => PowerLevelsContent.Moderator,
        "member" or "user" => 0,
        string number => long.TryParse(number, out long level) ? level : null,
    };

    /// <summary>Return the user ID of a member of <paramref name="roomId"/> given as a user ID, or by display name; null when no one matches.</summary>
    /// <remarks>A user ID is accepted even when the user is not a member, e.g. to unban or to ban someone before they join.</remarks>
    private string? ResolveMember(string roomId, string target)
    {
        // A user ID as it is
        if (UserId.TryParse(target, out _)) return target;
        // Return the one member with that display name
        return processor.Read(r =>
        {
            if (r is null || !r.TryGetRoom(roomId, out Room? room)) return null;
            List<string> matches = [.. room.Members.Values.Where(m => string.Equals(room.GetMemberDisplayName(m.UserId), target, StringComparison.CurrentCultureIgnoreCase)).Select(m => m.UserId)];
            return matches.Count == 1 ? matches[0] : null;
        });
    }

    /// <summary>Expand a leading <c>~</c> to the home directory.</summary>
    private static string ExpandHome(string path) => path == "~" || path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/')) : path;
}
