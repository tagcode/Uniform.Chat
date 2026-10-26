namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for devices, user-interactive auth and file messages.</summary>
public class DeviceTests
{
    private static readonly MatrixSession Session = new() { Homeserver = new Uri("https://hs.example/"), UserId = "@me:hs.example", DeviceId = "D", AccessToken = "tok" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Challenge = """{"flows":[{"stages":["m.login.password"]}],"session":"S1","params":{}}""";

    /// <summary>Devices are listed with their last activity.</summary>
    [Fact]
    public async Task CanListDevices()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """{"devices":[{"device_id":"D","display_name":"Laptop","last_seen_ip":"1.2.3.4","last_seen_ts":1700000000000}]}"""));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        Device device = Assert.Single(await client.GetDevicesAsync(Ct));

        // Assert
        Assert.Equal(("D", "Laptop", 1700000000000L), (device.DeviceId, device.DisplayName, device.LastSeenTs));
    }

    /// <summary>Deleting answers the password challenge with the session ID.</summary>
    [Fact]
    public async Task CanDeleteDevicesWithPassword()
    {
        // Arrange: challenge, then success
        int calls = 0;
        StubHandler handler = new(_ => ++calls == 1 ? StubHandler.Json(HttpStatusCode.Unauthorized, Challenge) : StubHandler.Json(HttpStatusCode.OK, "{}"));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        await client.DeleteDevicesAsync(["OLD1", "OLD2"], "secret", Ct);

        // Assert first attempt has no auth
        Assert.Equal("""{"devices":["OLD1","OLD2"]}""", handler.Requests[0].Body);

        // Assert second attempt answers the challenge
        JsonElement auth = JsonDocument.Parse(handler.Requests[1].Body!).RootElement.GetProperty("auth");
        Assert.Equal(AuthTypes.Password, auth.GetProperty("type").GetString());
        Assert.Equal("secret", auth.GetProperty("password").GetString());
        Assert.Equal("S1", auth.GetProperty("session").GetString());
        Assert.Equal("@me:hs.example", auth.GetProperty("identifier").GetProperty("user").GetString());
    }

    /// <summary>Without a password the challenge reaches the caller, who can ask for it.</summary>
    [Fact]
    public async Task CanSurfaceChallengeWithoutPassword()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.Unauthorized, Challenge));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        UserInteractiveAuthException ex = await Assert.ThrowsAsync<UserInteractiveAuthException>(() => client.DeleteDevicesAsync(["OLD"], null, Ct));

        // Assert
        Assert.True(ex.AcceptsPassword);
        Assert.Equal("S1", ex.Response.Session);
        Assert.Single(handler.Requests);
    }

    /// <summary>A wrong password comes back as another challenge with the error.</summary>
    [Fact]
    public async Task CanReportWrongPassword()
    {
        // Arrange: challenge, then challenge with error
        int calls = 0;
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.Unauthorized, ++calls == 1 ? Challenge : """{"flows":[{"stages":["m.login.password"]}],"session":"S1","errcode":"M_FORBIDDEN","error":"Invalid password"}"""));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        UserInteractiveAuthException ex = await Assert.ThrowsAsync<UserInteractiveAuthException>(() => client.DeleteDevicesAsync(["OLD"], "wrong", Ct));

        // Assert
        Assert.Equal("M_FORBIDDEN", ex.Response.Errcode);
        Assert.Equal("Invalid password", ex.Message);
    }

    /// <summary>An ordinary 401 with an errcode stays a <see cref="MatrixException"/>.</summary>
    [Fact]
    public async Task CanKeepUnknownTokenAsMatrixError()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.Unauthorized, """{"errcode":"M_UNKNOWN_TOKEN","error":"Invalid token"}"""));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act and assert
        MatrixException ex = await Assert.ThrowsAsync<MatrixException>(() => client.GetDevicesAsync(Ct));
        Assert.Equal(ErrorCodes.UnknownToken, ex.Errcode);
    }

    /// <summary>File messages pick their type from the MIME type and carry metadata.</summary>
    [Theory]
    [InlineData("image/png", MessageTypes.Image)]
    [InlineData("video/mp4", MessageTypes.Video)]
    [InlineData("audio/ogg", MessageTypes.Audio)]
    [InlineData("application/pdf", MessageTypes.File)]
    public void CanCreateFileContent(string contentType, string messageType)
    {
        // Act
        MessageContent content = MatrixClient.CreateFileContent(MxcUri.Parse("mxc://hs.example/abc"), "doc", contentType, 42);

        // Assert
        Assert.Equal(messageType, content.MessageType);
        Assert.Equal("mxc://hs.example/abc", content.Url);
        Assert.Equal((contentType, 42L), (content.Info?.Mimetype, content.Info?.Size));
    }

    /// <summary>Sending a file uploads it, then sends a message pointing at the upload.</summary>
    [Fact]
    public async Task CanSendFile()
    {
        // Arrange
        StubHandler handler = new(request => request.RequestUri!.AbsolutePath.Contains("/upload")
            ? StubHandler.Json(HttpStatusCode.OK, """{"content_uri":"mxc://hs.example/up"}""")
            : StubHandler.Json(HttpStatusCode.OK, """{"event_id":"$f"}"""));
        MatrixClient client = new(new HttpClient(handler), Session);
        using MemoryStream file = new([1, 2, 3, 4]);

        // Act
        string eventId = await client.SendFileAsync("!r:hs.example", file, "cat.png", "image/png", cancellationToken: Ct);

        // Assert
        Assert.Equal("$f", eventId);
        JsonElement body = JsonDocument.Parse(handler.Requests[1].Body!).RootElement;
        Assert.Equal("m.image", body.GetProperty("msgtype").GetString());
        Assert.Equal("mxc://hs.example/up", body.GetProperty("url").GetString());
        Assert.Equal(4, body.GetProperty("info").GetProperty("size").GetInt64());
    }
}
