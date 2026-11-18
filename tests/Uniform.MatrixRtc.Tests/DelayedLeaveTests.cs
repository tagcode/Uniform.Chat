namespace Uniform.LiveKit.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Uniform.LiveKit.Authorization;

/// <summary>Tests of <see cref="DelayedLeaveKeeper"/>.</summary>
public sealed class DelayedLeaveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLiveKit livekit = new();
    private readonly FakeHomeservers homeservers = new();

    /// <summary>Return a keeper over the fakes.</summary>
    private DelayedLeaveKeeper CreateKeeper() => new(() => new LiveKitAuthorizerOptions { Url = "wss://livekit.example.org", ApiKey = "key", ApiSecret = "a-secret-long-enough-for-livekit" }, homeservers, new HttpClient(livekit), time, NullLogger<DelayedLeaveKeeper>.Instance);

    /// <summary>Keep a member's leave: restarted before it goes off while the member is on LiveKit, and sent once the member is gone.</summary>
    [Fact]
    public async Task KeepUntilTheMemberLeaves()
    {
        // A member on LiveKit, its leave of a minute kept
        await using DelayedLeaveKeeper keeper = CreateKeeper();
        livekit.Present.Add(("room", "alice"));
        keeper.Keep("example.org", "delay1", TimeSpan.FromMinutes(1), "room", "alice");

        // Past four fifths of the delay: restarted, not sent
        await AdvanceAsync(TimeSpan.FromSeconds(55));
        Assert.Contains(("example.org", "delay1", "restart"), homeservers.Updates);
        Assert.DoesNotContain(homeservers.Updates, u => u.Action == "send");

        // Gone from LiveKit: sent, and the job ends
        livekit.Present.Clear();
        await AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(("example.org", "delay1", "send"), homeservers.Updates);
        // Done
        Assert.Empty(keeper.Members);
    }

    /// <summary>Leave a member that never joins LiveKit to its homeserver's delay, and stop when the homeserver no longer has the leave.</summary>
    [Fact]
    public async Task StopWithoutTheMember()
    {
        // Never on LiveKit
        await using DelayedLeaveKeeper keeper = CreateKeeper();
        keeper.Keep("example.org", "never", TimeSpan.FromSeconds(30), "room", "bob");
        await AdvanceAsync(TimeSpan.FromSeconds(40));
        Assert.DoesNotContain(homeservers.Updates, u => u.DelayId == "never" && u.Action == "send");
        Assert.Empty(keeper.Members);

        // On LiveKit, but the leave was sent meanwhile
        livekit.Present.Add(("room", "carol"));
        homeservers.Gone.Add("sent");
        keeper.Keep("example.org", "sent", TimeSpan.FromSeconds(30), "room", "carol");
        await AdvanceAsync(TimeSpan.FromSeconds(40));
        // Done
        Assert.Empty(keeper.Members);
    }

    /// <summary>Move the fake clock on in steps of the check interval, letting the jobs run between.</summary>
    private async Task AdvanceAsync(TimeSpan span)
    {
        // A check at a time
        for (TimeSpan passed = TimeSpan.Zero; passed < span; passed += TimeSpan.FromSeconds(1))
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>LiveKit's room service, answering GetParticipant from <see cref="Present"/>.</summary>
    private sealed class FakeLiveKit : HttpMessageHandler
    {
        /// <summary>Room and identity pairs on LiveKit.</summary>
        public ConcurrentBag<(string Room, string Identity)> Present { get; } = [];

        /// <summary>Answer 200 for a present participant, else Twirp's not found.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The asked participant
            JsonNode body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            bool present = Present.Contains((body["room"]!.GetValue<string>(), body["identity"]!.GetValue<string>()));
            // Done
            return new HttpResponseMessage(present ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new StringContent(present ? "{}" : """{"code":"not_found","msg":"participant not found"}""") };
        }
    }

    /// <summary>Homeservers recording the actions on delayed events.</summary>
    private sealed class FakeHomeservers : IDelayedEvents
    {
        /// <summary>Actions so far.</summary>
        public ConcurrentQueue<(string Server, string DelayId, string Action)> Updates { get; } = new();

        /// <summary>Delayed events the homeservers no longer have.</summary>
        public ConcurrentBag<string> Gone { get; } = [];

        /// <summary>Record, and answer whether the delayed event is there.</summary>
        public Task<bool> UpdateAsync(string serverName, string delayId, string action, CancellationToken cancellationToken)
        {
            // Done
            Updates.Enqueue((serverName, delayId, action));
            return Task.FromResult(!Gone.Contains(delayId));
        }
    }
}
