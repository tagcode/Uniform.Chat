namespace Uniform.Matrix.Server.Host.Ui;

using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Server.Accounts;
using global::Terminal.Gui.App;

/// <summary>Rooms of the server, with their members and directory listing; rooms can be listed or unlisted in the public room directory.</summary>
public sealed class RoomsWindow : AdminWindow<AdminRoom>
{
    private readonly AdminService admin;

    /// <summary>Build the window and load the rooms.</summary>
    public RoomsWindow(IApplication app, AdminService admin)
        : base(app, "Rooms", $"{"Room",-30} {"Alias",-28} {"Members",7}  Flags")
    {
        // Actions, then the list
        this.admin = admin;
        AddAction("_Publish", TogglePublished);
        Reload();
    }

    /// <summary>Load the rooms.</summary>
    protected override Task<IReadOnlyList<AdminRoom>> LoadAsync() => admin.GetRoomsAsync(CancellationToken.None);

    /// <summary>One room per row, by name or ID.</summary>
    protected override string FormatRow(AdminRoom room) => $"{Clip(room.Name ?? room.RoomId, 30),-30} {Clip(room.Alias, 28),-28} {room.Members,7}  {Flags(room)}";

    /// <summary>Details of the room.</summary>
    protected override string Describe(AdminRoom room) =>
        $"{room.RoomId}{(room.Name is null ? "" : $" ({room.Name})")}\nVersion {room.Version}, created by {room.Creator}; {room.Members} joined members. {(room.Encrypted ? "Encrypted; the server cannot read its messages." : "Not encrypted.")}";

    /// <summary>List or unlist the selected room in the public room directory.</summary>
    private void TogglePublished()
    {
        // Confirm the change
        if (Selected is not AdminRoom room) return;
        string question = room.Published ? $"Remove {room.Name ?? room.RoomId} from the public room directory?" : $"List {room.Name ?? room.RoomId} in the public room directory? Anyone on the server can find it; joining still follows the room's join rule.";
        if (!Dialogs.Confirm(Tui, "Publish", question)) return;
        Run("Publish", () => admin.SetPublishedAsync(room.RoomId, !room.Published, CancellationToken.None));
        Reload();
    }

    /// <summary>Flag words of a room.</summary>
    private static string Flags(AdminRoom room) => string.Join(", ", new[] { room.Published ? "public" : null, room.Encrypted ? "encrypted" : null }.OfType<string>());
}
