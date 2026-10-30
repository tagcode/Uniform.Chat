namespace Uniform.Chat.Terminal.Ui;

using System.Text.Json;
using Uniform.Chat.Terminal.Configuration;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using global::Terminal.Gui.Views;

// Message actions, composing replies and edits, typing notifications, nick completion, files and devices.
public sealed partial class MainWindow
{
    private static readonly TimeSpan TypingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TypingRenewal = TimeSpan.FromSeconds(20);

    private ClientEvent? replyTo;
    private string? editEventId;
    private readonly Dictionary<string, string> completedMentions = [];
    private List<(string Name, string UserId)>? completionCandidates;
    private int completionIndex;
    private int completionStart;
    private bool completing;
    private string? typingRoomId;
    private DateTimeOffset typingSentAt;

    /// <summary>Focus the message log on the newest message, to pick one for <see cref="ShowMessageActions"/>.</summary>
    private void SelectMessage()
    {
        // Focus and select
        timeline.SetFocus();
        timeline.SelectLast();
        timeline.SetNeedsDraw();
        logger.LogDebug("Selected message {EventId}, log focused: {Focused}", timeline.SelectedEntry?.EventId, timeline.HasFocus);
    }

    /// <summary>Show actions for the selected message: reply, react, edit, delete, save attachment, report, block the sender.</summary>
    private void ShowMessageActions()
    {
        // Assert selected event of the open room
        logger.LogDebug("Message actions for {EventId}", timeline.SelectedEntry?.EventId);
        if (timeline.SelectedEntry?.Event is not ClientEvent ev || currentRoomId is not string roomId) return;
        bool own = ev.Sender == ownUserId;
        bool message = ev.Type == EventTypes.RoomMessage && MessageFormatter.GetText(ev) is not null;
        ev.TryGetContent(out MessageContent? content);
        bool attachment = message && MxcUri.TryParse(content?.MediaUrl, out _);
        IReadOnlyList<MatrixLink> links = message ? [.. MatrixLink.FindAll(content?.FormattedBody).Concat(MatrixLink.FindAll(content?.Body)).Distinct().Where(l => l.Target != ownUserId)] : [];

        // Offer what applies; hotkeys must be unique, so React uses A
        List<(string Label, Action Run)> actions = [];
        if (message) actions.Add(("_Reply", () => StartReply(ev)));
        if (message) actions.Add(("Re_act", () => React(roomId, ev)));
        if (message && own && !attachment) actions.Add(("_Edit", () => StartEdit(ev)));
        if (attachment && content!.MessageType == MessageTypes.Image) actions.Add(("_View image", () => _ = ViewImageAsync(content)));
        if (attachment) actions.Add(("_Save file", () => _ = SaveAttachmentAsync(content!)));
        if (links.Count > 0) actions.Add(("Open _link", () => OpenLink(links)));
        if (own && ev.Unsigned?.RedactedBecause is null) actions.Add(("_Delete", () => DeleteMessage(roomId, ev)));
        if (!own && message) actions.Add(("Re_port", () => ReportMessage(roomId, ev)));
        if (!own) actions.Add(("_Block sender", () => SetIgnored(ev.Sender, ignored: true)));
        actions.Add(("_Close", () => { }));

        // Ask, then run after this dialog has fully closed, so a follow-up dialog does not receive the same key
        string preview = MessageFormatter.Snippet(timeline.SelectedEntry.Text, 60);
        int? choice = Dialogs.Choose(app, "Message", preview, [.. actions.Select(a => a.Label)]);
        input.SetFocus();
        if (choice is int index && index < actions.Count) app.Invoke(actions[index].Run);
    }

    /// <summary>Follow the room or user link of a message, asking which one when there are several.</summary>
    private void OpenLink(IReadOnlyList<MatrixLink> links)
    {
        // One link is followed, several are chosen from by number, which also keeps underscores in IDs from becoming hotkeys; the dialog has room for a few
        if (links.Count == 1)
        {
            FollowLink(links[0]);
            return;
        }
        List<MatrixLink> shown = [.. links.Take(5)];
        int? choice = Dialogs.Choose(app, "Open link", "Which room or user?", [.. shown.Select((l, i) => $"_{i + 1} {l.Target}"), "_Close"]);
        // Follow the chosen one after this dialog has closed
        if (choice is int index && index < shown.Count) app.Invoke(() => FollowLink(shown[index]));
    }

    /// <summary>Re-renders messages of the open room that <paramref name="events"/> redact, as entries are formatted once.</summary>
    private void ApplyRedactions(string roomId, IEnumerable<ClientEvent> events)
    {
        // Each redaction replaces its target with a redacted copy
        foreach (ClientEvent redaction in events.Where(e => e.Type == EventTypes.RoomRedaction))
        {
            string? targetId = redaction.Redacts ?? (redaction.TryGetContent(out RedactionContent? content) ? content.Redacts : null);
            if (targetId is null || timeline.FindEvent(targetId) is not ClientEvent target) continue;
            ClientEvent redacted = target with { Content = JsonDocument.Parse("{}").RootElement.Clone(), Unsigned = (target.Unsigned ?? new UnsignedData()) with { RedactedBecause = redaction } };
            if (Format(roomId, [redacted]).FirstOrDefault() is TimelineEntry entry) timeline.Replace(entry);
        }
    }

    /// <summary>Make the next message a reply to <paramref name="clientEvent"/>.</summary>
    private void StartReply(ClientEvent clientEvent)
    {
        // Enter reply mode
        editEventId = null;
        replyTo = clientEvent;
        string nick = processor.Read(r => currentRoomId is not null && r?.TryGetRoom(currentRoomId, out Room? room) == true ? room.GetMemberDisplayName(clientEvent.Sender) : clientEvent.Sender);
        SetInputPrefix($"[re {nick}] ");
    }

    /// <summary>Put the text of own message <paramref name="clientEvent"/> into the input line for editing.</summary>
    private void StartEdit(ClientEvent clientEvent)
    {
        // Enter edit mode with the current text
        replyTo = null;
        editEventId = clientEvent.EventId;
        input.Text = MessageFormatter.GetText(clientEvent) ?? "";
        input.MoveEnd();
        SetInputPrefix("[edit] ");
    }

    /// <summary>Leave reply or edit mode.</summary>
    private void CancelCompose()
    {
        // Clear mode and prefix
        replyTo = null;
        editEventId = null;
        SetInputPrefix("");
    }

    /// <summary>Show <paramref name="text"/> before the input line, e.g. the reply target.</summary>
    private void SetInputPrefix(string text)
    {
        // Relayout as the prefix width changes
        inputPrefix.Text = text;
        inputPrefix.SetNeedsLayout();
        SetNeedsLayout();
    }

    /// <summary>Queue a message, reply or edit to the open room, with mentions of completed nicks; Markdown is sent as HTML unless <paramref name="plain"/>.</summary>
    private void SendMessage(string text, bool emote, bool plain = false)
    {
        // Assert joined room
        if (text.Length == 0) return;
        if (currentRoomId is not string roomId || processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.Membership == Memberships.Join) != true)
        {
            Dialogs.Error(app, "No room", "Open a room you have joined first.");
            return;
        }

        // Mentions still in the text, and HTML for formatting and pills
        Dictionary<string, string> mentioned = completedMentions.Where(m => text.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value);
        List<string> mentions = [.. mentioned.Values.Distinct()];
        string? html = plain ? null : MarkdownComposer.ToHtml(text, mentioned);

        // Build content by mode
        object content = editEventId is string target ? MatrixClient.CreateEditContent(target, text, html)
            : replyTo is ClientEvent parent ? MatrixClient.CreateReplyContent(parent, text, ownUserId, mentions, html)
            : new MessageContent { MessageType = emote ? MessageTypes.Emote : MessageTypes.Text, Body = text, Format = html is null ? null : MessageFormats.Html, FormattedBody = html, Mentions = mentions.Count > 0 ? new Mentions { UserIds = mentions } : null };

        // Queue and reset composing
        _ = outbox.EnqueueAsync(roomId, EventTypes.RoomMessage, content);
        completedMentions.Clear();
        CancelCompose();
        SendTyping(roomId, false);
    }

    /// <summary>React to <paramref name="clientEvent"/> through the outbox.</summary>
    private void React(string roomId, ClientEvent clientEvent)
    {
        // Pick and queue
        string? key = ClientDialogs.PickReaction(app);
        if (key is null) return;
        _ = outbox.EnqueueAsync(roomId, EventTypes.Reaction, MatrixClient.CreateReactionContent(clientEvent.EventId, key));
    }

    /// <summary>Delete own message <paramref name="clientEvent"/> after confirmation.</summary>
    private void DeleteMessage(string roomId, ClientEvent clientEvent)
    {
        // Confirm and redact
        if (!Dialogs.Confirm(app, "Delete message", $"Delete \"{MessageFormatter.Snippet(MessageFormatter.GetText(clientEvent) ?? "", 40)}\" for everyone?")) return;
        _ = RunSafelyAsync("Deleting message", client => client.RedactAsync(roomId, clientEvent.EventId));
    }

    /// <summary>Download an attachment into the cache and copy it to the download folder, asking where first unless <see cref="UiOptions.AskWhereToSave"/> is off.</summary>
    private async Task SaveAttachmentAsync(MessageContent content)
    {
        // A free name in the download folder, which the user may change
        string name = AccountPaths.ToSafeName(content.Filename ?? content.Body ?? "download");
        string path = FreePath(GetDownloadDirectory(), name);
        if (UiOptions.AskWhereToSave)
        {
            if (Dialogs.Ask(app, "Save file", "Save as (a file, or a folder to save into):", path) is not string chosen) return;
            path = ExpandHome(chosen);
            if (Directory.Exists(path)) path = FreePath(path, name);
            else if (File.Exists(path) && !Dialogs.Confirm(app, "Save file", $"{path} exists. Replace it?")) return;
        }

        // Download into the cache, then copy
        ShowStatus($"Downloading {name}…");
        await RunSafelyAsync("Saving file", async client =>
        {
            StoredMedia media = await mediaCache.GetAsync(client, content);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.Copy(mediaCache.GetFullPath(media), path, overwrite: true);
            app.Invoke(() => { ShowStatus($"Saved {path}"); Dialogs.Info(app, "File saved", path); });
        });
    }

    /// <summary>Return a path for <paramref name="name"/> in <paramref name="directory"/> that is not taken, adding <c>(1)</c>, <c>(2)</c>… when needed.</summary>
    private static string FreePath(string directory, string name)
    {
        // First free name
        string path = Path.Combine(directory, name);
        for (int i = 1; File.Exists(path); i++) path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        // Return path
        return path;
    }

    /// <summary>Return <see cref="UiOptions.DownloadDirectory"/>, or <c>~/Downloads</c> when it exists, otherwise the home folder.</summary>
    private string GetDownloadDirectory()
    {
        // The setting first, then the usual downloads folder
        if (UiOptions.DownloadDirectory is { Length: > 0 } configured) return ExpandHome(configured);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = Path.Combine(home, "Downloads");
        // Return existing directory
        return Directory.Exists(downloads) ? downloads : home;
    }

    /// <summary>Offer to send the files dragged or pasted into the input line; return whether the line was files, so it is not sent as a message.</summary>
    private bool OfferDroppedFiles(string text)
    {
        // Only lines of nothing but existing files
        IReadOnlyList<string> files = DroppedFiles.Parse(text, File.Exists);
        if (files.Count == 0 || currentRoomId is null) return false;
        string list = string.Join("\n", files.Select(Path.GetFileName));
        if (!Dialogs.Confirm(app, "Send files", files.Count == 1 ? $"Send {list} to this room?" : $"Send these {files.Count} files to this room?\n\n{list}"))
        {
            // Back to the input line, to edit or send as text
            input.Text = text;
            return true;
        }
        // Done
        foreach (string file in files) SendFile(file);
        return true;
    }

    /// <summary>Pick a file and send it to the open room.</summary>
    private void SendFile()
    {
        // Assert room, then pick
        if (currentRoomId is null) return;
        if (Dialogs.PickFile(app, "Send file") is string path) SendFile(path);
    }

    /// <summary>Send the file at <paramref name="path"/> to the open room, encrypted when the room is.</summary>
    private void SendFile(string path)
    {
        // Assert room, and encryption when the room needs it
        if (currentRoomId is not string roomId) return;
        bool encrypted = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.IsEncrypted);
        if (encrypted && !crypto.Available)
        {
            Dialogs.Error(app, "Send file", $"This room is encrypted, but encryption is not available on this device.\n\n{crypto.UnavailableReason}");
            return;
        }

        // Encrypted rooms: encrypt and upload, then send the message through the outbox, which encrypts it
        string name = Path.GetFileName(path);
        ShowStatus($"Uploading {name}…");
        if (encrypted)
        {
            _ = RunSafelyAsync($"Sending {name}", async client =>
            {
                byte[] bytes = await File.ReadAllBytesAsync(path);
                EncryptedFile file = await client.UploadEncryptedAsync(bytes);
                await outbox.EnqueueAsync(roomId, EventTypes.RoomMessage, MatrixClient.CreateFileContent(file, name, MimeTypes.FromPath(path), bytes.Length));
                app.Invoke(() => ShowStatus($"Sent {name}"));
            });
            return;
        }

        // Others: upload and send directly
        _ = RunSafelyAsync($"Sending {name}", async client =>
        {
            await using FileStream file = File.OpenRead(path);
            await client.SendFileAsync(roomId, file, name, MimeTypes.FromPath(path));
            app.Invoke(() => ShowStatus($"Sent {name}"));
        });
    }

    /// <summary>Reset nick completion on edits and send typing notifications.</summary>
    private void OnInputChanged()
    {
        // Completion keeps its state only while cycling
        if (!completing) completionCandidates = null;

        // Typing while writing a message, not a command
        if (currentRoomId is not string roomId) return;
        bool writing = input.Text.Length > 0 && !input.Text.StartsWith('/');
        SendTyping(roomId, writing);
    }

    /// <summary>Tell the server whether the user is typing, renewing it while typing continues.</summary>
    private void SendTyping(string roomId, bool typing)
    {
        // Assert change or renewal
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool active = typingRoomId == roomId;
        if (typing && active && now - typingSentAt < TypingRenewal) return;
        if (!typing && !active) return;
        typingRoomId = typing ? roomId : null;
        typingSentAt = now;
        if (Client is not MatrixClient client) return;

        // Send in the background; failures only affect the indicator
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SetTypingAsync(roomId, typing, TypingTimeout);
            }
            catch (Exception ex)
            {
                logger.LogInformation("Typing notification for {RoomId} failed: {Error}", roomId, ex.Message);
            }
        });
    }

    /// <summary>Complete the last word of the input to a member name; repeated Tab cycles through matches.</summary>
    private void CompleteNick()
    {
        // Start a new completion from the last word
        string text = input.Text;
        if (completionCandidates is null)
        {
            completionStart = text.LastIndexOf(' ') + 1;
            string prefix = text[completionStart..];
            if (prefix.Length == 0 || currentRoomId is not string roomId) return;
            completionCandidates = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? FindCompletions(room, prefix) : []);
            completionIndex = 0;
        }
        else completionIndex++;
        if (completionCandidates.Count == 0) return;

        // Replace the word; a nick at the start gets a colon, like irssi
        (string name, string userId) = completionCandidates[completionIndex % completionCandidates.Count];
        completedMentions[name] = userId;
        completing = true;
        input.Text = text[..completionStart] + name + (completionStart == 0 ? ": " : " ");
        input.MoveEnd();
        completing = false;
    }

    /// <summary>Return active members whose display name or user ID starts with <paramref name="prefix"/>.</summary>
    private List<(string Name, string UserId)> FindCompletions(Room room, string prefix) => [.. room.Members.Values
        .Where(m => m.IsActive && m.UserId != ownUserId)
        .Select(m => (Name: room.GetMemberDisplayName(m.UserId), m.UserId))
        .Where(m => m.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase) || m.UserId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || m.UserId[1..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>List devices and rename or log out the chosen ones, asking for the password when the server requires it.</summary>
    private async Task ManageDevicesAsync()
    {
        // Assert connected
        if (Client is not MatrixClient client)
        {
            Dialogs.Error(app, "Not connected", "Wait until the client is connected.");
            return;
        }

        // Show, act, and show again until closed
        while (true)
        {
            IReadOnlyList<Device> devices;
            try
            {
                devices = await client.GetDevicesAsync();
                if (crypto.Available) await crypto.TrackUserAsync(ownUserId);
            }
            catch (Exception ex)
            {
                Dialogs.Error(app, "Devices", ex.Message);
                return;
            }
            Func<string, string>? trust = crypto.Available ? await DeviceTrustLabelsAsync() : null;
            if (ClientDialogs.ChooseDeviceAction(app, devices, client.DeviceId, trust) is not { } choice) return;
            await RunDeviceActionAsync(client, choice.Action, choice.Device, devices);
            if (choice.Action == 4) return;
        }
    }

    /// <summary>Run one action from <see cref="ClientDialogs.ChooseDeviceAction"/>.</summary>
    private async Task RunDeviceActionAsync(MatrixClient client, int action, Device? device, IReadOnlyList<Device> devices)
    {
        // Verify; the flow continues in dialogs as messages arrive
        if (action == 4 && device is not null)
        {
            if (device.DeviceId == client.DeviceId) Dialogs.Info(app, "Verify", "This is the device you are using. Pick another of your devices.");
            else await StartVerificationAsync(ownUserId, device.DeviceId);
            return;
        }

        // Rename
        if (action == 1 && device is not null)
        {
            string? name = Dialogs.Ask(app, "Rename device", $"Name for {device.DeviceId}:", device.DisplayName ?? "");
            if (name is not null) await RunSafelyAsync("Renaming device", c => c.RenameDeviceAsync(device.DeviceId, name));
            return;
        }

        // Pick devices to log out; this session ends with File → Log out instead
        List<string> targets = action == 2 && device is not null ? [device.DeviceId] : action == 3 ? [.. devices.Select(d => d.DeviceId)] : [];
        targets.Remove(client.DeviceId ?? "");
        if (targets.Count == 0)
        {
            Dialogs.Info(app, "Devices", "Nothing to log out. Use File → Log out to end this session.");
            return;
        }
        if (!Dialogs.Confirm(app, "Log out devices", $"Log out {targets.Count} device(s): {string.Join(", ", targets)}?")) return;

        // Delete, answering the password challenge
        string? password = Dialogs.Ask(app, "Confirm", $"Password for {ownUserId}:", secret: true);
        if (password is null) return;
        await RunSafelyAsync("Logging out devices", c => c.DeleteDevicesAsync(targets, password));
    }
}
