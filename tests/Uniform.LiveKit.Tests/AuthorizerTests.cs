namespace Uniform.LiveKit.Tests;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Uniform.LiveKit.Authorization;
using Uniform.LiveKit.Protocol;

/// <summary>Tests of <see cref="LiveKitAuthorizer"/> and <see cref="FederationOpenIdVerifier"/>.</summary>
public sealed class AuthorizerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeNetwork network = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
    private const string Secret = "a-secret-long-enough-for-livekit";

    /// <summary>Return an authorizer over the fake network, where example.org has full access.</summary>
    private LiveKitAuthorizer CreateAuthorizer() => new(() => new LiveKitAuthorizerOptions { Url = "wss://livekit.example.org", ApiKey = "key", ApiSecret = Secret, FullAccessHomeservers = ["example.org"] }, new FederationOpenIdVerifier(network.CreateClient()), network.CreateClient(), time, NullLogger<LiveKitAuthorizer>.Instance);

    /// <summary>Return a request of <paramref name="token"/> from <paramref name="server"/> claiming <paramref name="user"/>.</summary>
    private static TokenRequest Request(string token, string server, string user, string? delayId = null) => new()
    {
        RoomId = "!room:example.org",
        SlotId = MatrixRtcNames.RoomCallSlot,
        OpenIdToken = new OpenIdToken { AccessToken = token, TokenType = "Bearer", MatrixServerName = server, ExpiresIn = 3600 },
        Member = new CallMember { Id = "member", ClaimedUserId = user, ClaimedDeviceId = "DEVICE" },
        DelayId = delayId,
    };

    /// <summary>Give a full-access user a publishing token, after creating the room in LiveKit.</summary>
    [Fact]
    public async Task AuthorizeFullAccess()
    {
        // Alice's token at her homeserver
        network.Users["alice-token"] = "@alice:example.org";
        SfuResponse answer = await CreateAuthorizer().AuthorizeAsync(Request("alice-token", "example.org", "@alice:example.org"), Ct);

        // The token is for her identity in the room's alias, and may publish
        string room = MatrixRtcNames.RoomAlias("!room:example.org", MatrixRtcNames.RoomCallSlot);
        Assert.Equal("wss://livekit.example.org", answer.Url);
        Assert.True(AccessToken.TryRead(answer.Jwt, Secret, time.GetUtcNow(), out AccessToken? claims));
        Assert.Equal(MatrixRtcNames.Identity("@alice:example.org", "DEVICE", "member"), claims!.Identity);
        Assert.Equal(new VideoGrant { RoomJoin = true, RoomCreate = false, Hidden = false, Room = room, CanPublish = true, CanPublishData = true, CanSubscribe = true, CanUpdateOwnMetadata = false }, claims.Video);
        Assert.Equal(time.GetUtcNow().AddHours(1).ToUnixTimeSeconds(), claims.Expires);

        // The room was created over HTTPS with a token that may create rooms
        Assert.Contains("https://example.org:8448/_matrix/federation/v1/openid/userinfo?access_token=alice-token", network.Requests);
        Assert.Contains("https://livekit.example.org/twirp/livekit.RoomService/CreateRoom", network.Requests);
        (string body, string? authorization) = Assert.Single(network.LiveKitCalls);
        using JsonDocument created = JsonDocument.Parse(body);
        Assert.Equal(room, created.RootElement.GetProperty("name").GetString());
        Assert.Equal(300, created.RootElement.GetProperty("empty_timeout").GetInt32());
        Assert.Equal(20, created.RootElement.GetProperty("departure_timeout").GetInt32());
        Assert.True(AccessToken.TryRead(authorization!, Secret, time.GetUtcNow(), out AccessToken? admin));
        // Done
        Assert.True(admin!.Video!.RoomCreate);
    }

    /// <summary>Give a user of another homeserver a token that only subscribes, and create no room.</summary>
    [Fact]
    public async Task AuthorizeRestricted()
    {
        // Bob of a server that delegates
        network.Users["bob-token"] = "@bob:other.org";
        network.Delegations["other.org"] = "matrix.other.org:8443";
        SfuResponse answer = await CreateAuthorizer().AuthorizeLegacyAsync(new LegacySfuRequest { Room = "!room:example.org", DeviceId = "BOBDEV", OpenIdToken = new OpenIdToken { AccessToken = "bob-token", MatrixServerName = "other.org" } }, Ct);

        // Done
        Assert.True(AccessToken.TryRead(answer.Jwt, Secret, time.GetUtcNow(), out AccessToken? claims));
        Assert.Equal("@bob:other.org:BOBDEV", claims!.Identity);
        Assert.False(claims.Video!.CanPublish);
        Assert.False(claims.Video.CanPublishData);
        Assert.True(claims.Video.CanSubscribe);
        Assert.Contains("https://matrix.other.org:8443/_matrix/federation/v1/openid/userinfo?access_token=bob-token", network.Requests);
        Assert.Empty(network.LiveKitCalls);
    }

    /// <summary>Refuse unknown tokens, another user's claim, a server vouching for others' users, delayed leaves of restricted users and missing fields.</summary>
    [Fact]
    public async Task Refuse()
    {
        // Tokens: Alice's, and one that a rogue server answers with her
        network.Users["alice-token"] = "@alice:example.org";
        network.Users["rogue-token"] = "@alice:example.org";
        network.Users["bob-token"] = "@bob:other.org";
        LiveKitAuthorizer authorizer = CreateAuthorizer();

        // Each refused with its status
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<AuthorizationException>(() => authorizer.AuthorizeAsync(Request("nope", "example.org", "@alice:example.org"), Ct))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<AuthorizationException>(() => authorizer.AuthorizeAsync(Request("alice-token", "example.org", "@carol:example.org"), Ct))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<AuthorizationException>(() => authorizer.AuthorizeAsync(Request("rogue-token", "rogue.org", "@alice:example.org"), Ct))).Status);
        AuthorizationException delayed = await Assert.ThrowsAsync<AuthorizationException>(() => authorizer.AuthorizeAsync(Request("bob-token", "other.org", "@bob:other.org", "delay"), Ct));
        Assert.Equal((HttpStatusCode.BadRequest, AuthorizationError.BadJson), (delayed.Status, delayed.ErrorCode));
        AuthorizationException missing = await Assert.ThrowsAsync<AuthorizationException>(() => authorizer.AuthorizeAsync(Request("alice-token", "example.org", "@alice:example.org") with { SlotId = "" }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        // Done
        Assert.Empty(network.LiveKitCalls);
    }

    /// <summary>With membership checked, give tokens to joined users only, on both endpoints, before creating any room.</summary>
    [Fact]
    public async Task RequireJoinedMembers()
    {
        // Alice joined to the room, Carol not
        network.Users["alice-token"] = "@alice:example.org";
        network.Users["carol-token"] = "@carol:example.org";
        Members members = new() { Joined = { ("!room:example.org", "@alice:example.org") } };
        LiveKitAuthorizer authorizer = new(() => new LiveKitAuthorizerOptions { Url = "wss://livekit.example.org", ApiKey = "key", ApiSecret = Secret, FullAccessHomeservers = ["example.org"] }, new FederationOpenIdVerifier(network.CreateClient()), network.CreateClient(), time, NullLogger<LiveKitAuthorizer>.Instance, members);

        // Carol is refused on both endpoints
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<AuthorizationException>(() => authorizer.AuthorizeAsync(Request("carol-token", "example.org", "@carol:example.org"), Ct))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<AuthorizationException>(() => authorizer.AuthorizeLegacyAsync(new LegacySfuRequest { Room = "!room:example.org", DeviceId = "CAROLDEV", OpenIdToken = new OpenIdToken { AccessToken = "carol-token", MatrixServerName = "example.org" } }, Ct))).Status);
        Assert.Empty(network.LiveKitCalls);

        // Done
        SfuResponse answer = await authorizer.AuthorizeAsync(Request("alice-token", "example.org", "@alice:example.org"), Ct);
        Assert.True(AccessToken.TryRead(answer.Jwt, Secret, time.GetUtcNow(), out _));
    }

    /// <summary>Joined members of rooms, as a homeserver would know them.</summary>
    private sealed class Members : IRoomMembership
    {
        /// <summary>Joined room and user pairs.</summary>
        public HashSet<(string RoomId, string UserId)> Joined { get; } = [];

        /// <summary>Return whether the pair is in <see cref="Joined"/>.</summary>
        public Task<bool> IsJoinedAsync(string roomId, string userId, CancellationToken cancellationToken) => Task.FromResult(Joined.Contains((roomId, userId)));
    }

    /// <summary>Tell ports after host names and IPv6 addresses.</summary>
    [Fact]
    public void FindPorts()
    {
        // Done
        Assert.False(FederationOpenIdVerifier.HasPort("example.org"));
        Assert.True(FederationOpenIdVerifier.HasPort("example.org:8448"));
        Assert.False(FederationOpenIdVerifier.HasPort("[2001:db8::1]"));
        Assert.True(FederationOpenIdVerifier.HasPort("[2001:db8::1]:443"));
        Assert.Equal("https://example.org:8448", LiveKitRoomService.HttpUrl("wss://example.org:8448/"));
        Assert.Equal("http://localhost:7880", LiveKitRoomService.HttpUrl("ws://localhost:7880"));
    }
}
