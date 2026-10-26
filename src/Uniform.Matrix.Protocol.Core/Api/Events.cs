namespace Uniform.Matrix.Protocol.Core;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Event with a type and content; used as is for account data and ephemeral events.</summary>
public record MatrixEvent
{
    /// <summary>Event type, see <see cref="EventTypes"/>.</summary>
    public required string Type { get; init; }
    /// <summary>Raw event content; reads with <see cref="TryGetContent{T}"/>.</summary>
    public JsonElement Content { get; init; }

    /// <summary>Deserialize <see cref="Content"/> as <typeparamref name="T"/>; false when content is missing or malformed.</summary>
    /// <example><code><![CDATA[
    /// if (!ev.TryGetContent(out MessageContent? message)) continue;
    /// Console.WriteLine(message.Body);
    /// ]]></code></example>
    public bool TryGetContent<T>([NotNullWhen(true)] out T? content) where T : class
    {
        // Assert content is an object
        content = null;
        if (Content.ValueKind != JsonValueKind.Object) return false;

        // Deserialize; other clients may send malformed content
        try
        {
            content = Content.Deserialize<T>(MatrixJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }
        // Return whether content was read
        return content is not null;
    }
}

/// <summary>Room event as returned by sync, messages and state endpoints, see <see href="https://spec.matrix.org/latest/client-server-api/#room-event-format">room event format</see>.</summary>
public sealed record ClientEvent : MatrixEvent
{
    /// <summary>Globally unique event ID.</summary>
    public required string EventId { get; init; }
    /// <summary>User ID of the sender.</summary>
    public required string Sender { get; init; }
    /// <summary>Server timestamp in milliseconds since Unix epoch.</summary>
    public required long OriginServerTs { get; init; }
    /// <summary>Room of the event; omitted inside sync responses.</summary>
    public string? RoomId { get; init; }
    /// <summary>State key; non-null exactly for state events.</summary>
    public string? StateKey { get; init; }
    /// <summary>Redacted event ID of <see cref="EventTypes.RoomRedaction"/> in room versions before 11.</summary>
    public string? Redacts { get; init; }
    /// <summary>Extra data added by the server.</summary>
    public UnsignedData? Unsigned { get; init; }
    /// <summary><see cref="OriginServerTs"/> as a timestamp.</summary>
    [JsonIgnore]
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(OriginServerTs);
    /// <summary>Whether this is a state event.</summary>
    [JsonIgnore]
    public bool IsState => StateKey is not null;
}

/// <summary>Server-added <see href="https://spec.matrix.org/latest/client-server-api/#definition-clientevent_unsigneddata">unsigned data</see> of a <see cref="ClientEvent"/>.</summary>
public sealed record UnsignedData
{
    /// <summary>Milliseconds since the event was sent.</summary>
    public long? Age { get; init; }
    /// <summary>Transaction ID, present only for the sender's own events.</summary>
    public string? TransactionId { get; init; }
    /// <summary>Previous content of a state event.</summary>
    public JsonElement? PrevContent { get; init; }
    /// <summary>Redaction that removed this event's content.</summary>
    public ClientEvent? RedactedBecause { get; init; }
}

/// <summary>Partial state of an invited room, see <see href="https://spec.matrix.org/latest/client-server-api/#stripped-state">stripped state</see>.</summary>
public sealed record StrippedStateEvent : MatrixEvent
{
    /// <summary>State key.</summary>
    public required string StateKey { get; init; }
    /// <summary>User ID of the sender.</summary>
    public required string Sender { get; init; }
}

/// <summary>Presence update of <see cref="Sender"/>, content <see cref="PresenceContent"/>.</summary>
public sealed record PresenceEvent : MatrixEvent
{
    /// <summary>User whose presence changed.</summary>
    public required string Sender { get; init; }
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#send-to-device-messaging">Send-to-device</see> event of <see cref="SyncResponse.ToDevice"/>.</summary>
public sealed record ToDeviceEvent : MatrixEvent
{
    /// <summary>User ID of the sender.</summary>
    public required string Sender { get; init; }
}
