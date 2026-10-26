namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>In-memory stand-in for a homeserver's key endpoints and to-device delivery, shared by several <see cref="OlmMachine"/>s.</summary>
internal sealed class FakeKeyServer
{
    private readonly Dictionary<string, Dictionary<string, JsonObject>> deviceKeys = [];
    private readonly Dictionary<(string User, string Device), Queue<(string Id, JsonObject Key)>> oneTimeKeys = [];
    private readonly Dictionary<(string User, string Device), (string Id, JsonObject Key)> fallbackKeys = [];
    private readonly Dictionary<(string User, string Device), List<ToDeviceEvent>> inboxes = [];
    private readonly Dictionary<(string User, string Usage), JsonObject> crossSigningKeys = [];
    private readonly Dictionary<(string User, string Type), JsonObject> accountData = [];
    private readonly Dictionary<string, (KeyBackupVersion Version, KeysBackup Keys)> backups = [];
    private int backupVersions;

    /// <summary>Times cross-signing keys were uploaded.</summary>
    public int CrossSigningUploads { get; private set; }

    /// <summary>Stored global account data of a user.</summary>
    public JsonObject? GetAccountData(string user, string type) => accountData.GetValueOrDefault((user, type));

    /// <summary>Number of sessions in a user's current backup.</summary>
    public int BackedUpSessions(string user) => backups.TryGetValue(user, out (KeyBackupVersion, KeysBackup Keys) backup) ? backup.Keys.Rooms.Values.Sum(r => r.Sessions.Count) : 0;

    /// <summary>Unclaimed one-time keys of a device.</summary>
    public int OneTimeKeyCount(string user, string device) => oneTimeKeys.TryGetValue((user, device), out Queue<(string, JsonObject)>? keys) ? keys.Count : 0;

    /// <summary>Raw uploaded device keys, e.g. to tamper with in tests.</summary>
    public JsonObject? GetDeviceKeys(string user, string device) => deviceKeys.GetValueOrDefault(user)?.GetValueOrDefault(device);

    /// <summary>Return a transport acting as <paramref name="user"/> on <paramref name="device"/>.</summary>
    public IKeyTransport For(string user, string device) => new Transport(this, user, device);

    /// <summary>Return and clear the to-device events waiting for a device, as a sync would deliver them.</summary>
    public SyncResponse Sync(string user, string device)
    {
        // Take the inbox
        List<ToDeviceEvent> events = inboxes.GetValueOrDefault((user, device)) ?? [];
        inboxes.Remove((user, device));
        // Return a sync with them and the key count
        return new SyncResponse
        {
            NextBatch = Guid.NewGuid().ToString(),
            ToDevice = new EventList<ToDeviceEvent> { Events = events },
            DeviceOneTimeKeysCount = new Dictionary<string, int> { [EncryptionAlgorithms.SignedCurve25519] = OneTimeKeyCount(user, device) },
            DeviceUnusedFallbackKeyTypes = fallbackKeys.ContainsKey((user, device)) ? [EncryptionAlgorithms.SignedCurve25519] : [],
        };
    }

    /// <summary>Transport bound to one device.</summary>
    private sealed class Transport(FakeKeyServer server, string user, string device) : IKeyTransport
    {
        /// <summary>Store uploaded keys.</summary>
        public Task<KeysUploadResponse> UploadKeysAsync(JsonObject? keys, JsonObject? oneTime, JsonObject? fallback, CancellationToken cancellationToken)
        {
            // Device keys
            if (keys is not null)
            {
                if (!server.deviceKeys.TryGetValue(user, out Dictionary<string, JsonObject>? devices)) server.deviceKeys[user] = devices = [];
                devices[device] = (JsonObject)keys.DeepClone();
            }

            // One-time and fallback keys
            if (!server.oneTimeKeys.TryGetValue((user, device), out Queue<(string, JsonObject)>? queue)) server.oneTimeKeys[(user, device)] = queue = new();
            foreach ((string id, JsonNode? key) in oneTime ?? []) queue.Enqueue((id, (JsonObject)key!.DeepClone()));
            foreach ((string id, JsonNode? key) in fallback ?? []) server.fallbackKeys[(user, device)] = (id, (JsonObject)key!.DeepClone());
            // Return counts
            return Task.FromResult(new KeysUploadResponse { OneTimeKeyCounts = new Dictionary<string, int> { [EncryptionAlgorithms.SignedCurve25519] = queue.Count } });
        }

        /// <summary>Return uploaded device keys.</summary>
        public Task<KeysQueryResponse> QueryKeysAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
        {
            // Copy per user
            Dictionary<string, IReadOnlyDictionary<string, JsonObject>> result = userIds.ToDictionary(u => u, u => (IReadOnlyDictionary<string, JsonObject>)(server.deviceKeys.GetValueOrDefault(u) ?? []).ToDictionary(d => d.Key, d => (JsonObject)d.Value.DeepClone()));
            // Cross-signing keys; the user-signing key only for its owner
            Dictionary<string, JsonObject> CrossSigning(string usage, Func<string, bool> visible) =>
                userIds.Where(visible).Where(u => server.crossSigningKeys.ContainsKey((u, usage))).ToDictionary(u => u, u => (JsonObject)server.crossSigningKeys[(u, usage)].DeepClone());
            // Return response
            return Task.FromResult(new KeysQueryResponse
            {
                DeviceKeys = result,
                MasterKeys = CrossSigning(CrossSigningUsages.Master, _ => true),
                SelfSigningKeys = CrossSigning(CrossSigningUsages.SelfSigning, _ => true),
                UserSigningKeys = CrossSigning(CrossSigningUsages.UserSigning, u => u == user),
            });
        }

        /// <summary>Store cross-signing keys, replacing old ones like a server after the password.</summary>
        public Task UploadCrossSigningKeysAsync(JsonObject masterKey, JsonObject selfSigningKey, JsonObject userSigningKey, string? password, CancellationToken cancellationToken)
        {
            // Store each
            server.crossSigningKeys[(user, CrossSigningUsages.Master)] = (JsonObject)masterKey.DeepClone();
            server.crossSigningKeys[(user, CrossSigningUsages.SelfSigning)] = (JsonObject)selfSigningKey.DeepClone();
            server.crossSigningKeys[(user, CrossSigningUsages.UserSigning)] = (JsonObject)userSigningKey.DeepClone();
            server.CrossSigningUploads++;
            // Done
            return Task.CompletedTask;
        }

        /// <summary>Merge signatures into device keys or master keys.</summary>
        public Task UploadSignaturesAsync(IReadOnlyDictionary<string, Dictionary<string, JsonObject>> signatures, CancellationToken cancellationToken)
        {
            // Target by device ID or master public key
            foreach ((string userId, Dictionary<string, JsonObject> byKey) in signatures)
                foreach ((string keyId, JsonObject signed) in byKey)
                {
                    JsonObject? target = server.deviceKeys.GetValueOrDefault(userId)?.GetValueOrDefault(keyId);
                    if (target is null && server.crossSigningKeys.TryGetValue((userId, CrossSigningUsages.Master), out JsonObject? master) && master["keys"]?[$"ed25519:{keyId}"] is not null) target = master;
                    if (target is null) continue;
                    foreach ((string signer, JsonNode? keys) in signed["signatures"]!.AsObject())
                        foreach ((string signingKeyId, JsonNode? signature) in keys!.AsObject())
                        {
                            target["signatures"] ??= new JsonObject();
                            target["signatures"]![signer] ??= new JsonObject();
                            target["signatures"]![signer]![signingKeyId] = signature!.GetValue<string>();
                        }
                }
            // Done
            return Task.CompletedTask;
        }

        /// <summary>Return account data.</summary>
        public Task<JsonObject?> GetAccountDataAsync(string type, CancellationToken cancellationToken) => Task.FromResult((JsonObject?)server.accountData.GetValueOrDefault((user, type))?.DeepClone());

        /// <summary>Store account data.</summary>
        public Task SetAccountDataAsync(string type, JsonObject content, CancellationToken cancellationToken)
        {
            // Copy
            server.accountData[(user, type)] = (JsonObject)content.DeepClone();
            // Done
            return Task.CompletedTask;
        }

        /// <summary>Return the current backup.</summary>
        public Task<KeyBackupVersion?> GetKeyBackupVersionAsync(CancellationToken cancellationToken) => Task.FromResult(server.backups.TryGetValue(user, out (KeyBackupVersion Version, KeysBackup) backup) ? backup.Version : null);

        /// <summary>Create a backup version, replacing the current one.</summary>
        public Task<string> CreateKeyBackupVersionAsync(string algorithm, JsonObject authData, CancellationToken cancellationToken)
        {
            // New empty version
            string version = (++server.backupVersions).ToString();
            server.backups[user] = (new KeyBackupVersion { Algorithm = algorithm, AuthData = (JsonObject)authData.DeepClone(), Version = version }, new KeysBackup());
            // Return version
            return Task.FromResult(version);
        }

        /// <summary>Store keys in the current backup version.</summary>
        public Task UploadRoomKeysAsync(string version, KeysBackup keys, CancellationToken cancellationToken)
        {
            // Must be the current version
            if (!server.backups.TryGetValue(user, out (KeyBackupVersion Version, KeysBackup Keys) backup) || backup.Version.Version != version) throw new MatrixException(HttpStatusCode.Forbidden, "M_WRONG_ROOM_KEYS_VERSION", "Wrong backup version.");
            foreach ((string roomId, RoomKeyBackup room) in keys.Rooms)
            {
                if (!backup.Keys.Rooms.TryGetValue(roomId, out RoomKeyBackup? stored)) backup.Keys.Rooms[roomId] = stored = new RoomKeyBackup();
                foreach ((string sessionId, KeyBackupData data) in room.Sessions) stored.Sessions[sessionId] = data;
            }
            // Done
            return Task.CompletedTask;
        }

        /// <summary>Return the keys of a backup version.</summary>
        public Task<KeysBackup> GetRoomKeysAsync(string version, CancellationToken cancellationToken)
        {
            // Must be the current version
            if (!server.backups.TryGetValue(user, out (KeyBackupVersion Version, KeysBackup Keys) backup) || backup.Version.Version != version) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "No such backup.");
            // Return a copy through JSON
            return Task.FromResult(JsonSerializer.Deserialize<KeysBackup>(JsonSerializer.Serialize(backup.Keys, MatrixJson.Options), MatrixJson.Options)!);
        }

        /// <summary>Pop a one-time key per device, falling back to the fallback key.</summary>
        public Task<KeysClaimResponse> ClaimKeysAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> devices, CancellationToken cancellationToken)
        {
            // Claim per device
            Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonObject>>> result = [];
            foreach ((string userId, IReadOnlyList<string> deviceIds) in devices)
            {
                Dictionary<string, IReadOnlyDictionary<string, JsonObject>> byDevice = [];
                foreach (string deviceId in deviceIds)
                {
                    (string Id, JsonObject Key)? key = server.oneTimeKeys.TryGetValue((userId, deviceId), out Queue<(string, JsonObject)>? queue) && queue.Count > 0 ? queue.Dequeue() : server.fallbackKeys.TryGetValue((userId, deviceId), out (string, JsonObject) fallback) ? fallback : null;
                    if (key is (string id, JsonObject value)) byDevice[deviceId] = new Dictionary<string, JsonObject> { [id] = (JsonObject)value.DeepClone() };
                }
                result[userId] = byDevice;
            }
            // Return claimed keys
            return Task.FromResult(new KeysClaimResponse { OneTimeKeys = result });
        }

        /// <summary>Queue to-device messages for their recipients.</summary>
        public Task SendToDeviceAsync(string eventType, IReadOnlyDictionary<string, Dictionary<string, object>> messages, CancellationToken cancellationToken)
        {
            // Deliver to each inbox; * means every device of the user
            foreach ((string userId, Dictionary<string, object> byDevice) in messages)
                foreach ((string deviceId, object content) in byDevice)
                    foreach (string target in deviceId == "*" ? [.. (server.deviceKeys.GetValueOrDefault(userId) ?? []).Keys] : new[] { deviceId })
                    {
                        if (!server.inboxes.TryGetValue((userId, target), out List<ToDeviceEvent>? inbox)) server.inboxes[(userId, target)] = inbox = [];
                        inbox.Add(new ToDeviceEvent { Type = eventType, Sender = user, Content = JsonSerializer.SerializeToElement(content, content.GetType(), MatrixJson.Options) });
                    }
            // Done
            return Task.CompletedTask;
        }
    }
}
