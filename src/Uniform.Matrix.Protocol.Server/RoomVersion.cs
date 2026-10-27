namespace Uniform.Matrix.Protocol.Server;

/// <summary>A <see href="https://spec.matrix.org/latest/rooms/">room version</see>: the rules of event format, redaction and authorization a room follows.</summary>
public sealed record RoomVersion
{
    /// <summary>Version 10: integer power levels, knock_restricted joins.</summary>
    public static readonly RoomVersion V10 = new() { Id = "10", CreateHasCreator = true, Redaction = RedactionRules.V10 };
    /// <summary>Version 11: the create event's sender is the creator, clarified redaction.</summary>
    public static readonly RoomVersion V11 = new() { Id = "11", CreateHasCreator = false, Redaction = RedactionRules.V11 };
    /// <summary>Version 12: the room ID is the create event's hash, and the creators have unlimited power.</summary>
    public static readonly RoomVersion V12 = new() { Id = "12", CreateHasCreator = false, Redaction = RedactionRules.V11, RoomIdIsCreateHash = true };

    /// <summary>Versions this server can create and joins, by ID.</summary>
    public static IReadOnlyDictionary<string, RoomVersion> Supported { get; } = new Dictionary<string, RoomVersion> { [V10.Id] = V10, [V11.Id] = V11, [V12.Id] = V12 };

    /// <summary>Version of new rooms unless the client asks for another.</summary>
    public static RoomVersion Default => V10;

    /// <summary>Version ID, e.g. <c>10</c>.</summary>
    public required string Id { get; init; }
    /// <summary>Whether <c>m.room.create</c> must name the creator in <c>content.creator</c>; from version 11 the sender is the creator.</summary>
    public bool CreateHasCreator { get; init; }
    /// <summary>Which keys survive redaction.</summary>
    public RedactionRules Redaction { get; init; }
    /// <summary>Whether the room ID is <c>!</c> and the create event's reference hash, the create event has no <c>room_id</c> and is left out of <c>auth_events</c>, and the creators (the sender and <c>additional_creators</c>) have unlimited power; from version 12.</summary>
    public bool RoomIdIsCreateHash { get; init; }

    /// <summary>Return the room ID of a room whose create event has ID <paramref name="createEventId"/>, when <see cref="RoomIdIsCreateHash"/>.</summary>
    public static string RoomIdOf(string createEventId) => "!" + createEventId[1..];

    /// <summary>Return the version of ID <paramref name="id"/>, or null when not supported.</summary>
    public static RoomVersion? Find(string? id) => id is not null && Supported.TryGetValue(id, out RoomVersion? version) ? version : null;
}

/// <summary>Redaction algorithm of a room version.</summary>
public enum RedactionRules
{
    /// <summary>Versions 6 to 10: <c>origin</c>, <c>membership</c> and <c>prev_state</c> are kept, and only <c>creator</c> of the create content.</summary>
    V10,
    /// <summary>Version 11: the create content, <c>redacts</c>, the <c>invite</c> power level and third-party invite signatures are kept.</summary>
    V11,
}
