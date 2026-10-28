namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client.Avalonia.Core.Services;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>A room of a public room directory.</summary>
/// <param name="Room">The directory's entry.</param>
/// <param name="Server">Server whose directory listed it; empty for the user's own.</param>
public sealed record PublicRoomItem(PublicRoom Room, string Server)
{
    /// <summary>Name, else alias, else ID.</summary>
    public string Title => Room.Name ?? Room.CanonicalAlias ?? Room.RoomId;

    /// <summary>Alias and member count.</summary>
    public string Details => string.Join(" · ", new[] { Room.CanonicalAlias, Room.NumJoinedMembers == 1 ? "1 member" : $"{Room.NumJoinedMembers} members" }.OfType<string>());

    /// <summary>Topic, or empty.</summary>
    public string Topic => Room.Topic ?? "";

    /// <summary>Whether there is a topic to show.</summary>
    public bool HasTopic => Topic.Length > 0;

    /// <summary>Link to join the room by: its alias, else its ID, through the server that listed it.</summary>
    public MatrixLink Link => new() { Target = Room.CanonicalAlias ?? Room.RoomId, Via = Server.Length > 0 ? [Server] : [] };
}

/// <summary>The public room directory of the user's server or another one: search it and join a room.</summary>
public sealed partial class DirectoryViewModel : ViewModelBase
{
    private const int PageSize = 50;
    private readonly ClientConnection connection;
    private readonly IUiDispatcher ui;
    private readonly Func<MatrixLink, Task<bool>> join;
    private int generation;

    /// <summary>Create the directory of <paramref name="server"/>; <paramref name="join"/> joins a chosen room and returns whether it did.</summary>
    public DirectoryViewModel(ClientConnection connection, IUiDispatcher ui, string server, Func<MatrixLink, Task<bool>> join)
    {
        // Dependencies and the server shown last
        this.connection = connection;
        this.ui = ui;
        this.join = join;
        Server = server;
    }

    /// <summary>Server whose directory to show, e.g. <c>matrix.org</c>; empty for the user's own.</summary>
    [ObservableProperty]
    public partial string Server { get; set; } = "";

    /// <summary>Text to search for in names, topics and aliases; empty lists the directory.</summary>
    [ObservableProperty]
    public partial string SearchTerm { get; set; } = "";

    /// <summary>Rooms found.</summary>
    public ObservableCollection<PublicRoomItem> Rooms { get; } = [];

    /// <summary>Whether a search runs.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>What happened, e.g. that nothing was found or why searching failed; empty for none.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>Search the directory; a later search replaces an earlier one still running.</summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        // Assert logged in
        if (!connection.Session.WhenReady.IsCompletedSuccessfully) return;
        int current = ++generation;
        string server = Server.Trim();
        string term = SearchTerm.Trim();
        IsBusy = true;
        Message = server.Length > 0 ? $"Asking {server}…" : "Searching…";
        Rooms.Clear();

        // Ask, through the user's server
        try
        {
            MatrixClient client = await connection.Session.WhenReady;
            PublicRoomsResponse page = await client.GetPublicRoomsAsync(server.Length > 0 ? server : null, PageSize, searchTerm: term.Length > 0 ? term : null);
            if (current != generation) return;
            foreach (PublicRoom room in page.Chunk) Rooms.Add(new PublicRoomItem(room, server));
            Message = Rooms.Count == 0 ? "No rooms found." : "";
        }
        catch (Exception exception) when (exception is MatrixException or HttpRequestException or TaskCanceledException)
        {
            connection.Logger.LogInformation("Room directory of {Server} failed: {Error}", server.Length > 0 ? server : "this server", exception.Message);
            if (current == generation) Message = $"Search failed: {exception.Message}";
        }
        finally
        {
            if (current == generation) IsBusy = false;
        }
    }

    /// <summary>Join <paramref name="room"/> through the server that listed it.</summary>
    [RelayCommand]
    private async Task JoinAsync(PublicRoomItem room)
    {
        // The chat opens it when it arrives
        Message = $"Joining {room.Title}…";
        bool joined = await join(room.Link);
        ui.Post(() => Message = joined ? "" : $"Could not join {room.Title}.");
    }
}
