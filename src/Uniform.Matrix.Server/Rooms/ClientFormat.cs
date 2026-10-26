namespace Uniform.Matrix.Server.Rooms;

using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;

/// <summary>The <see href="https://spec.matrix.org/latest/client-server-api/#room-event-format">client format</see> of stored events: the PDU without graph fields, with the event ID and <c>unsigned</c> data.</summary>
internal static class ClientFormat
{
    /// <summary>Convert a stored event.</summary>
    /// <param name="row">The event.</param>
    /// <param name="now">Current time, for <c>unsigned.age</c>.</param>
    /// <param name="transactionId">Client transaction ID, given only to the device that sent the event.</param>
    /// <param name="redactedBecause">Client form of the redaction, when the event is redacted.</param>
    /// <param name="prevContent">Content of the state this event replaced, when known.</param>
    public static JsonObject ToClient(EventRow row, long now, string? transactionId = null, JsonObject? redactedBecause = null, JsonObject? prevContent = null)
    {
        // Fields clients see
        JsonObject pdu = JsonNode.Parse(row.Json)!.AsObject();
        JsonObject client = new()
        {
            ["content"] = pdu["content"]?.DeepClone() ?? new JsonObject(),
            ["event_id"] = row.EventId,
            ["origin_server_ts"] = row.OriginServerTs,
            ["room_id"] = row.RoomId,
            ["sender"] = row.Sender,
            ["type"] = row.Type,
        };
        if (row.StateKey is not null) client["state_key"] = row.StateKey;

        // Redactions name their target at the top level in every room version, as clients read it there
        if (row.Type == EventTypes.RoomRedaction && (pdu["redacts"] ?? pdu["content"]?["redacts"]) is JsonNode redacts) client["redacts"] = redacts.DeepClone();

        // Unsigned data
        JsonObject unsigned = new() { ["age"] = Math.Max(0, now - row.OriginServerTs) };
        if (transactionId is not null) unsigned["transaction_id"] = transactionId;
        if (redactedBecause is not null) unsigned["redacted_because"] = redactedBecause;
        if (prevContent is not null) unsigned["prev_content"] = prevContent;
        client["unsigned"] = unsigned;
        // Return client event
        return client;
    }
}
