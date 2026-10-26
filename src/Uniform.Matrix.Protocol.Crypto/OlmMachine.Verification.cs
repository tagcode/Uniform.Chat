namespace Uniform.Matrix.Protocol.Crypto;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>Stage of a <see cref="Verification"/>.</summary>
public enum VerificationState
{
    /// <summary>Requested; waiting for the other side to accept, or for us to accept an incoming request.</summary>
    Requested,
    /// <summary>Accepted; key agreement is running.</summary>
    Ready,
    /// <summary>Emoji are ready to compare; confirm with <see cref="OlmMachine.ConfirmVerificationAsync"/> if they match.</summary>
    ShowingSas,
    /// <summary>We confirmed; waiting for the other side.</summary>
    Confirmed,
    /// <summary>Both sides confirmed and the keys were verified.</summary>
    Done,
    /// <summary>Cancelled by either side, see <see cref="Verification.CancelReason"/>.</summary>
    Cancelled,
}

/// <summary>One of the 64 <see href="https://spec.matrix.org/latest/client-server-api/#sas-method-emoji">SAS emoji</see>.</summary>
/// <param name="Symbol">The emoji.</param>
/// <param name="Name">English name from the spec.</param>
public sealed record SasEmoji(string Symbol, string Name);

/// <summary>A <see href="https://spec.matrix.org/latest/client-server-api/#short-authentication-string-sas-verification">SAS verification</see> with another device, over to-device messages.</summary>
public sealed class Verification
{
    /// <summary>Create a verification; see <see cref="OlmMachine.RequestVerificationAsync"/>.</summary>
    internal Verification(string transactionId, string otherUserId, string? otherDeviceId, bool weRequested)
    {
        // Identity of the flow
        TransactionId = transactionId;
        OtherUserId = otherUserId;
        OtherDeviceId = otherDeviceId;
        WeRequested = weRequested;
    }

    /// <summary>ID shared by all messages of this verification.</summary>
    public string TransactionId { get; }
    /// <summary>User being verified; ourselves when verifying our own device.</summary>
    public string OtherUserId { get; }
    /// <summary>Device being verified; unknown until it accepts our request.</summary>
    public string? OtherDeviceId { get; internal set; }
    /// <summary>Whether we sent the request.</summary>
    public bool WeRequested { get; }
    /// <summary>Current stage.</summary>
    public VerificationState State { get; internal set; }
    /// <summary>Emoji to compare, from <see cref="VerificationState.ShowingSas"/> on.</summary>
    public IReadOnlyList<SasEmoji> Emoji { get; internal set; } = [];
    /// <summary>Numbers to compare instead of emoji.</summary>
    public IReadOnlyList<int> Decimals { get; internal set; } = [];
    /// <summary>Cancel code, e.g. <c>m.user</c> or <c>m.mismatched_sas</c>.</summary>
    public string? CancelCode { get; internal set; }
    /// <summary>Human-readable cancel reason.</summary>
    public string? CancelReason { get; internal set; }

    /// <summary>Devices our request went to, so the others can be told when one accepts.</summary>
    internal List<string> RequestedDevices { get; } = [];
    /// <summary>Content of the start message, ours or theirs.</summary>
    internal JsonObject? StartContent { get; set; }
    /// <summary>Whether we sent the start message.</summary>
    internal bool WeStarted { get; set; }
    /// <summary>Commitment from their accept message, when we started.</summary>
    internal string? Commitment { get; set; }
    /// <summary>Our ephemeral key exchange until established.</summary>
    internal Sas? Sas { get; set; }
    /// <summary>Our ephemeral public key.</summary>
    internal string? OurKey { get; set; }
    /// <summary>Their ephemeral public key.</summary>
    internal string? TheirKey { get; set; }
    /// <summary>Shared secret after the key exchange.</summary>
    internal EstablishedSas? Established { get; set; }
    /// <summary>Their MAC message, kept until we confirm.</summary>
    internal JsonObject? TheirMac { get; set; }
    /// <summary>Whether their MACs were verified.</summary>
    internal bool TheirMacVerified { get; set; }
    /// <summary>Whether they sent done.</summary>
    internal bool DoneReceived { get; set; }

    /// <summary>Free the key exchange.</summary>
    internal void Release()
    {
        // Key material is not needed after done or cancel
        Sas?.Dispose();
        Established?.Dispose();
        Sas = null;
        Established = null;
    }
}

// SAS emoji verification of devices.
public sealed partial class OlmMachine
{
    private const string SasMethod = "m.sas.v1";
    private const string KeyAgreement = "curve25519-hkdf-sha256";
    private const string MacMethod = "hkdf-hmac-sha256.v2";
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Verification> verifications = new();

    /// <summary>The <see href="https://spec.matrix.org/latest/client-server-api/#sas-method-emoji">SAS emoji table</see>, by index.</summary>
    public static IReadOnlyList<SasEmoji> SasEmojiTable { get; } =
    [
        new("🐶", "Dog"), new("🐱", "Cat"), new("🦁", "Lion"), new("🐎", "Horse"), new("🦄", "Unicorn"), new("🐷", "Pig"), new("🐘", "Elephant"), new("🐰", "Rabbit"),
        new("🐼", "Panda"), new("🐓", "Rooster"), new("🐧", "Penguin"), new("🐢", "Turtle"), new("🐟", "Fish"), new("🐙", "Octopus"), new("🦋", "Butterfly"), new("🌷", "Flower"),
        new("🌳", "Tree"), new("🌵", "Cactus"), new("🍄", "Mushroom"), new("🌏", "Globe"), new("🌙", "Moon"), new("☁️", "Cloud"), new("🔥", "Fire"), new("🍌", "Banana"),
        new("🍎", "Apple"), new("🍓", "Strawberry"), new("🌽", "Corn"), new("🍕", "Pizza"), new("🎂", "Cake"), new("❤️", "Heart"), new("😀", "Smiley"), new("🤖", "Robot"),
        new("🎩", "Hat"), new("👓", "Glasses"), new("🔧", "Spanner"), new("🎅", "Santa"), new("👍", "Thumbs Up"), new("☂️", "Umbrella"), new("⌛", "Hourglass"), new("⏰", "Clock"),
        new("🎁", "Gift"), new("💡", "Light Bulb"), new("📕", "Book"), new("✏️", "Pencil"), new("📎", "Paperclip"), new("✂️", "Scissors"), new("🔒", "Lock"), new("🔑", "Key"),
        new("🔨", "Hammer"), new("☎️", "Telephone"), new("🏁", "Flag"), new("🚂", "Train"), new("🚲", "Bicycle"), new("✈️", "Aeroplane"), new("🚀", "Rocket"), new("🏆", "Trophy"),
        new("⚽", "Ball"), new("🎸", "Guitar"), new("🎺", "Trumpet"), new("🔔", "Bell"), new("⚓", "Anchor"), new("🎧", "Headphones"), new("📁", "Folder"), new("📌", "Pin"),
    ];

    /// <summary>Raised when a verification starts, changes state or ends; handlers run inside the machine, so marshal UI work.</summary>
    public event Action<Verification>? VerificationChanged;

    /// <summary>Verifications in progress, ours and incoming; safe to read from <see cref="VerificationChanged"/>.</summary>
    public IReadOnlyList<Verification> Verifications => [.. verifications.Values.Where(v => v.State is not (VerificationState.Done or VerificationState.Cancelled))];

    /// <summary>Ask <paramref name="userId"/>'s devices, or one of them, to verify; for our own user, our other devices.</summary>
    public async Task<Verification> RequestVerificationAsync(string userId, string? deviceId = null, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Target devices
            await RefreshDeviceListAsync(userId, cancellationToken);
            List<string> devices = [.. GetDevices(userId).Where(d => !(d.UserId == UserId && d.DeviceId == DeviceId) && (deviceId is null || d.DeviceId == deviceId)).Select(d => d.DeviceId)];
            if (devices.Count == 0) throw new InvalidOperationException(deviceId is null ? $"{userId} has no other devices to verify." : $"Device {deviceId} of {userId} is not known.");

            // Request
            Verification verification = new(NewRequestId(), userId, deviceId, weRequested: true);
            verification.RequestedDevices.AddRange(devices);
            verifications[verification.TransactionId] = verification;
            JsonObject content = new() { ["from_device"] = DeviceId, ["methods"] = new JsonArray(SasMethod), ["timestamp"] = time.GetUtcNow().ToUnixTimeMilliseconds(), ["transaction_id"] = verification.TransactionId };
            await transport.SendToDeviceAsync(EventTypes.KeyVerificationRequest, ToDevices(userId, devices, content), cancellationToken);
            // Return verification
            Changed(verification);
            return verification;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Accept an incoming request; the requester then starts the emoji exchange.</summary>
    public async Task AcceptVerificationAsync(Verification verification, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Assert an incoming request
            if (verification.WeRequested || verification.State != VerificationState.Requested || verification.OtherDeviceId is null) return;

            // Ready
            verification.State = VerificationState.Ready;
            JsonObject content = new() { ["from_device"] = DeviceId, ["methods"] = new JsonArray(SasMethod), ["transaction_id"] = verification.TransactionId };
            await SendVerificationAsync(verification, EventTypes.KeyVerificationReady, content, cancellationToken);
            Changed(verification);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Confirm that the emoji match: sends our MACs, and finishes when theirs check out.</summary>
    public async Task ConfirmVerificationAsync(Verification verification, CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Assert emoji shown
            if (verification.State != VerificationState.ShowingSas || verification.Established is not EstablishedSas sas) return;

            // MAC our device key, and our master key when we trust it
            Dictionary<string, string> keys = new() { [$"ed25519:{DeviceId}"] = account.IdentityKeys.Ed25519 };
            if (GetIdentity(UserId) is UserIdentity identity && IsIdentityTrusted(identity)) keys[$"ed25519:{identity.MasterKey}"] = identity.MasterKey;
            string baseInfo = $"MATRIX_KEY_VERIFICATION_MAC{UserId}{DeviceId}{verification.OtherUserId}{verification.OtherDeviceId}{verification.TransactionId}";
            JsonObject macs = [];
            foreach ((string keyId, string key) in keys) macs[keyId] = sas.CalculateMac(key, baseInfo + keyId);
            string keyIds = string.Join(',', keys.Keys.Order(StringComparer.Ordinal));
            JsonObject content = new() { ["transaction_id"] = verification.TransactionId, ["mac"] = macs, ["keys"] = sas.CalculateMac(keyIds, baseInfo + "KEY_IDS") };

            // Send, then check theirs if already here
            verification.State = VerificationState.Confirmed;
            await SendVerificationAsync(verification, EventTypes.KeyVerificationMac, content, cancellationToken);
            Changed(verification);
            if (verification.TheirMac is not null) await VerifyTheirMacAsync(verification, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Cancel a verification, e.g. when the emoji do not match.</summary>
    /// <param name="verification">Verification to cancel.</param>
    /// <param name="code">Cancel code; <c>m.user</c> when the user declined, <c>m.mismatched_sas</c> when the emoji differ.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CancelVerificationAsync(Verification verification, string code = "m.user", CancellationToken cancellationToken = default)
    {
        // One operation at a time
        await gate.WaitAsync(cancellationToken);
        try
        {
            await CancelAsync(verification, code, code == "m.mismatched_sas" ? "The emoji did not match." : "Cancelled by the user.", cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Dispatch a received <c>m.key.verification.*</c> event.</summary>
    private async Task HandleVerificationEventAsync(string type, string sender, JsonObject content, CancellationToken cancellationToken)
    {
        // New requests
        if (content["transaction_id"]?.GetValue<string>() is not string transactionId) return;
        if (type == EventTypes.KeyVerificationRequest)
        {
            await ReceiveRequestAsync(sender, transactionId, content, cancellationToken);
            return;
        }

        // Messages of a known verification, from its other side
        if (!verifications.TryGetValue(transactionId, out Verification? verification) || verification.OtherUserId != sender) return;
        if (verification.State is VerificationState.Done or VerificationState.Cancelled) return;
        string? fromDevice = content["from_device"]?.GetValue<string>();
        if (verification.OtherDeviceId is not null && fromDevice is not null && fromDevice != verification.OtherDeviceId) return;
        try
        {
            if (type == EventTypes.KeyVerificationReady) await ReceiveReadyAsync(verification, fromDevice, cancellationToken);
            else if (type == EventTypes.KeyVerificationStart) await ReceiveStartAsync(verification, fromDevice, content, cancellationToken);
            else if (type == EventTypes.KeyVerificationAccept) await ReceiveAcceptAsync(verification, content, cancellationToken);
            else if (type == EventTypes.KeyVerificationKey) await ReceiveKeyAsync(verification, content, cancellationToken);
            else if (type == EventTypes.KeyVerificationMac) await ReceiveMacAsync(verification, content, cancellationToken);
            else if (type == EventTypes.KeyVerificationDone) await ReceiveDoneAsync(verification, cancellationToken);
            else if (type == EventTypes.KeyVerificationCancel) ReceiveCancel(verification, content);
        }
        catch (Exception ex) when (ex is OlmException or InvalidOperationException or FormatException)
        {
            await CancelAsync(verification, "m.unexpected_message", ex.Message, cancellationToken);
        }
    }

    /// <summary>Record an incoming request from a known device, when recent and for SAS.</summary>
    private async Task ReceiveRequestAsync(string sender, string transactionId, JsonObject content, CancellationToken cancellationToken)
    {
        // Not our own request, not stale, SAS supported
        if (content["from_device"]?.GetValue<string>() is not string fromDevice || (sender == UserId && fromDevice == DeviceId) || verifications.ContainsKey(transactionId)) return;
        DateTimeOffset sent = DateTimeOffset.FromUnixTimeMilliseconds(content["timestamp"]?.GetValue<long>() ?? 0);
        if (time.GetUtcNow() - sent > RequestLifetime || sent - time.GetUtcNow() > TimeSpan.FromMinutes(5)) return;
        if (content["methods"] is not JsonArray methods || !methods.Any(m => m?.GetValue<string>() == SasMethod)) return;

        // Known device only
        await RefreshDeviceListAsync(sender, cancellationToken);
        if (!GetDevices(sender).Any(d => d.DeviceId == fromDevice)) return;
        Verification verification = new(transactionId, sender, fromDevice, weRequested: false);
        verifications[transactionId] = verification;
        Changed(verification);
    }

    /// <summary>Our request was accepted: tell the other devices, then starts SAS.</summary>
    private async Task ReceiveReadyAsync(Verification verification, string? fromDevice, CancellationToken cancellationToken)
    {
        // Assert our pending request
        if (!verification.WeRequested || verification.State != VerificationState.Requested || fromDevice is null || !verification.RequestedDevices.Contains(fromDevice)) return;
        verification.OtherDeviceId = fromDevice;
        verification.State = VerificationState.Ready;

        // Others need not answer any more
        List<string> others = [.. verification.RequestedDevices.Where(d => d != fromDevice)];
        JsonObject cancel = new() { ["transaction_id"] = verification.TransactionId, ["code"] = "m.accepted", ["reason"] = "Verified on another device." };
        if (others.Count > 0) await transport.SendToDeviceAsync(EventTypes.KeyVerificationCancel, ToDevices(verification.OtherUserId, others, cancel), cancellationToken);

        // Start SAS
        JsonObject start = new()
        {
            ["from_device"] = DeviceId,
            ["method"] = SasMethod,
            ["key_agreement_protocols"] = new JsonArray(KeyAgreement),
            ["hashes"] = new JsonArray("sha256"),
            ["message_authentication_codes"] = new JsonArray(MacMethod),
            ["short_authentication_string"] = new JsonArray("decimal", "emoji"),
            ["transaction_id"] = verification.TransactionId,
        };
        verification.StartContent = start;
        verification.WeStarted = true;
        await SendVerificationAsync(verification, EventTypes.KeyVerificationStart, start, cancellationToken);
        Changed(verification);
    }

    /// <summary>They started SAS: check the methods and answers with our commitment.</summary>
    private async Task ReceiveStartAsync(Verification verification, string? fromDevice, JsonObject content, CancellationToken cancellationToken)
    {
        // Both started: the lower user and device ID wins
        if (verification.WeStarted)
        {
            bool weWin = string.CompareOrdinal($"{UserId}|{DeviceId}", $"{verification.OtherUserId}|{fromDevice}") < 0;
            if (weWin) return;
            verification.WeStarted = false;
        }
        if (verification.State is not (VerificationState.Ready or VerificationState.Requested) || verification.Sas is not null) return;
        verification.OtherDeviceId ??= fromDevice;

        // Methods we support
        bool supported = content["method"]?.GetValue<string>() == SasMethod
            && Contains(content["key_agreement_protocols"], KeyAgreement)
            && Contains(content["hashes"], "sha256")
            && Contains(content["message_authentication_codes"], MacMethod)
            && Contains(content["short_authentication_string"], "emoji");
        if (!supported)
        {
            await CancelAsync(verification, "m.unknown_method", "No common SAS method.", cancellationToken);
            return;
        }

        // Commit to our key over their start content
        verification.StartContent = content;
        verification.Sas = Sas.Create();
        verification.OurKey = verification.Sas.PublicKey;
        verification.State = VerificationState.Ready;
        JsonObject accept = new()
        {
            ["transaction_id"] = verification.TransactionId,
            ["method"] = SasMethod,
            ["key_agreement_protocol"] = KeyAgreement,
            ["hash"] = "sha256",
            ["message_authentication_code"] = MacMethod,
            ["short_authentication_string"] = new JsonArray("decimal", "emoji"),
            ["commitment"] = CreateCommitment(verification.OurKey, content),
        };
        await SendVerificationAsync(verification, EventTypes.KeyVerificationAccept, accept, cancellationToken);
        Changed(verification);
    }

    /// <summary>They accepted our start: keep their commitment and sends our key.</summary>
    private async Task ReceiveAcceptAsync(Verification verification, JsonObject content, CancellationToken cancellationToken)
    {
        // Assert our start and their choice of our methods
        if (!verification.WeStarted || verification.Sas is not null) return;
        if (content["key_agreement_protocol"]?.GetValue<string>() != KeyAgreement || content["message_authentication_code"]?.GetValue<string>() != MacMethod || content["commitment"]?.GetValue<string>() is not string commitment)
        {
            await CancelAsync(verification, "m.unknown_method", "Unsupported SAS method chosen.", cancellationToken);
            return;
        }

        // Our key
        verification.Commitment = commitment;
        verification.Sas = Sas.Create();
        verification.OurKey = verification.Sas.PublicKey;
        await SendVerificationAsync(verification, EventTypes.KeyVerificationKey, new JsonObject { ["transaction_id"] = verification.TransactionId, ["key"] = verification.OurKey }, cancellationToken);
    }

    /// <summary>Their key arrived: check the commitment or answers with ours, then derives the emoji.</summary>
    private async Task ReceiveKeyAsync(Verification verification, JsonObject content, CancellationToken cancellationToken)
    {
        // Assert our key exchange
        if (verification.Sas is not Sas sas || verification.TheirKey is not null || content["key"]?.GetValue<string>() is not string theirKey) return;
        verification.TheirKey = theirKey;

        // Starter checks the commitment; the other side answers with its key
        if (verification.WeStarted && CreateCommitment(theirKey, verification.StartContent!) != verification.Commitment)
        {
            await CancelAsync(verification, "m.mismatched_commitment", "The key does not match the commitment.", cancellationToken);
            return;
        }
        if (!verification.WeStarted) await SendVerificationAsync(verification, EventTypes.KeyVerificationKey, new JsonObject { ["transaction_id"] = verification.TransactionId, ["key"] = verification.OurKey }, cancellationToken);

        // Shared secret and the short authentication string
        verification.Established = sas.Establish(theirKey);
        verification.Sas = null;
        sas.Dispose();
        (string startUser, string startDevice, string startKey, string acceptUser, string acceptDevice, string acceptKey) = verification.WeStarted
            ? (UserId, DeviceId, verification.OurKey!, verification.OtherUserId, verification.OtherDeviceId!, theirKey)
            : (verification.OtherUserId, verification.OtherDeviceId!, theirKey, UserId, DeviceId, verification.OurKey!);
        byte[] bytes = verification.Established.GetBytes($"MATRIX_KEY_VERIFICATION_SAS|{startUser}|{startDevice}|{startKey}|{acceptUser}|{acceptDevice}|{acceptKey}|{verification.TransactionId}");
        verification.Emoji = [.. EstablishedSas.EmojiIndices(bytes).Select(i => SasEmojiTable[i])];
        verification.Decimals = EstablishedSas.Decimals(bytes);
        verification.State = VerificationState.ShowingSas;
        Changed(verification);
    }

    /// <summary>Their MACs arrived; checked once we have confirmed too.</summary>
    private async Task ReceiveMacAsync(Verification verification, JsonObject content, CancellationToken cancellationToken)
    {
        // Keep, and check if we already confirmed
        if (verification.Established is null || verification.TheirMac is not null) return;
        verification.TheirMac = content;
        if (verification.State == VerificationState.Confirmed) await VerifyTheirMacAsync(verification, cancellationToken);
    }

    /// <summary>Check their MACs over their device key and master key, then mark what was verified, signs it, and send done.</summary>
    private async Task VerifyTheirMacAsync(Verification verification, CancellationToken cancellationToken)
    {
        // Their key list
        EstablishedSas sas = verification.Established!;
        JsonObject content = verification.TheirMac!;
        string baseInfo = $"MATRIX_KEY_VERIFICATION_MAC{verification.OtherUserId}{verification.OtherDeviceId}{UserId}{DeviceId}{verification.TransactionId}";
        if (content["mac"] is not JsonObject macs || content["keys"]?.GetValue<string>() is not string keysMac || !sas.VerifyMac(string.Join(',', macs.Select(m => m.Key).Order(StringComparer.Ordinal)), baseInfo + "KEY_IDS", keysMac))
        {
            await CancelAsync(verification, "m.key_mismatch", "The list of keys did not verify.", cancellationToken);
            return;
        }

        // Each key we know; the device key is required
        DeviceInfo? device = GetDevices(verification.OtherUserId).FirstOrDefault(d => d.DeviceId == verification.OtherDeviceId);
        UserIdentity? identity = GetIdentity(verification.OtherUserId);
        bool deviceVerified = false, masterVerified = false;
        foreach ((string keyId, JsonNode? macNode) in macs)
        {
            string? mac = macNode?.GetValue<string>();
            if (device is not null && keyId == $"ed25519:{device.DeviceId}")
            {
                deviceVerified = mac is not null && sas.VerifyMac(device.Ed25519Key, baseInfo + keyId, mac);
                if (!deviceVerified) break;
            }
            else if (identity is not null && keyId == $"ed25519:{identity.MasterKey}")
            {
                masterVerified = mac is not null && sas.VerifyMac(identity.MasterKey, baseInfo + keyId, mac);
                if (!masterVerified) break;
            }
        }
        if (!deviceVerified || (macs.ContainsKey($"ed25519:{identity?.MasterKey}") && !masterVerified))
        {
            await CancelAsync(verification, "m.key_mismatch", "A key did not verify.", cancellationToken);
            return;
        }

        // Remember, and cross-sign what we can
        verification.TheirMacVerified = true;
        await MarkDeviceVerifiedAsync(device!, cancellationToken);
        if (masterVerified) await UpdateIdentityAsync(verification.OtherUserId, i => i with { Trusted = true }, cancellationToken);
        await SignVerifiedAsync(verification.OtherUserId, device!, masterVerified, cancellationToken);

        // Done; finished once they are done too
        await SendVerificationAsync(verification, EventTypes.KeyVerificationDone, new JsonObject { ["transaction_id"] = verification.TransactionId }, cancellationToken);
        if (verification.DoneReceived) Finish(verification);
    }

    /// <summary>After a verification: signs our own device with the self-signing key and asks it for missing secrets, or signs another user's master key.</summary>
    private async Task SignVerifiedAsync(string userId, DeviceInfo device, bool masterVerified, CancellationToken cancellationToken)
    {
        // Our own device
        if (userId == UserId)
        {
            await SignOwnDevicesAsync([device.DeviceId], cancellationToken);
            await RequestMissingSecretsAsync(device, cancellationToken);
            return;
        }

        // Another user's identity
        if (masterVerified) await SignUserAsync(userId, cancellationToken);
    }

    /// <summary>They are done; finished when their MACs verified.</summary>
    private Task ReceiveDoneAsync(Verification verification, CancellationToken cancellationToken)
    {
        // Finish or wait for our check
        verification.DoneReceived = true;
        if (verification.TheirMacVerified) Finish(verification);
        // Done
        return Task.CompletedTask;
    }

    /// <summary>They cancelled.</summary>
    private void ReceiveCancel(Verification verification, JsonObject content)
    {
        // Record and release
        verification.CancelCode = content["code"]?.GetValue<string>() ?? "m.user";
        verification.CancelReason = content["reason"]?.GetValue<string>() ?? "Cancelled by the other side.";
        verification.State = VerificationState.Cancelled;
        verification.Release();
        Changed(verification);
    }

    /// <summary>Cancel with <paramref name="code"/> and tell the other side.</summary>
    private async Task CancelAsync(Verification verification, string code, string reason, CancellationToken cancellationToken)
    {
        // Assert not finished
        if (verification.State is VerificationState.Done or VerificationState.Cancelled) return;
        verification.CancelCode = code;
        verification.CancelReason = reason;
        verification.State = VerificationState.Cancelled;
        verification.Release();

        // Tell the chosen device, or every requested one
        JsonObject content = new() { ["transaction_id"] = verification.TransactionId, ["code"] = code, ["reason"] = reason };
        IEnumerable<string> devices = verification.OtherDeviceId is string device ? [device] : verification.RequestedDevices;
        await transport.SendToDeviceAsync(EventTypes.KeyVerificationCancel, ToDevices(verification.OtherUserId, devices, content), cancellationToken);
        Changed(verification);
    }

    /// <summary>Mark a verification done.</summary>
    private void Finish(Verification verification)
    {
        // Release and announce
        verification.State = VerificationState.Done;
        verification.Release();
        Changed(verification);
    }

    /// <summary>Send a verification message to the other device.</summary>
    private Task SendVerificationAsync(Verification verification, string type, JsonObject content, CancellationToken cancellationToken) =>
        transport.SendToDeviceAsync(type, ToDevices(verification.OtherUserId, [verification.OtherDeviceId!], content), cancellationToken);

    /// <summary>Return the accept commitment: SHA-256 of the public key followed by the canonical start content, unpadded base64.</summary>
    private static string CreateCommitment(string publicKey, JsonObject startContent)
    {
        // Hash key and canonical JSON
        string canonical = CanonicalJson.Serialize(JsonSerializer.SerializeToElement(startContent));
        // Return hash
        return Base64.EncodeUnpadded(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey + canonical)));
    }

    /// <summary>Return whether a JSON array contains <paramref name="value"/>.</summary>
    private static bool Contains(JsonNode? array, string value) => array is JsonArray items && items.Any(i => i?.GetValue<string>() == value);

    /// <summary>Raise <see cref="VerificationChanged"/>.</summary>
    private void Changed(Verification verification) => VerificationChanged?.Invoke(verification);
}
