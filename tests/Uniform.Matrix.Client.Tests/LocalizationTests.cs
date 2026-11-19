namespace Uniform.Matrix.Client.Tests;

using System.Globalization;
using System.Resources;
using System.Text.Json;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Testing;

/// <summary>The library's texts, such as event descriptions: every one in Finnish too, and shown in the current language.</summary>
public class LocalizationTests
{
    private static readonly ResourceManager Texts = new("Uniform.Matrix.Client.Localization.Strings", typeof(MatrixWorker).Assembly);

    /// <summary>Return every culture the library's texts are translated to.</summary>
    public static TheoryData<string> Cultures => LocalizationChecks.Cultures(rightToLeft: true);

    /// <summary>Every English text is in <paramref name="culture"/>, in each plural form the language uses, with the same placeholders.</summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void TranslateEveryText(string culture) => LocalizationChecks.AssertTranslated(Texts, culture, hotkeys: false);

    /// <summary>An event is described in the current language: English by default, Finnish once the thread's language is, with the names filled in.</summary>
    [Fact]
    public void DescribeInCurrentLanguage()
    {
        // A room where Bob joins
        RoomStore store = new("@me:hs.example");
        ClientEvent joined = new() { Type = EventTypes.RoomMember, Sender = "@bob:hs.example", StateKey = "@bob:hs.example", EventId = "$j", OriginServerTs = 1, Content = JsonSerializer.SerializeToElement(new MemberContent { Membership = Memberships.Join, DisplayName = "Bob" }, MatrixJson.Options) };
        store.Apply(new SyncResponse { NextBatch = "b", Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { ["!r:hs.example"] = new() { Timeline = new Timeline { Events = [joined] } } } } });
        store.TryGetRoom("!r:hs.example", out Room? room);
        Assert.Equal("Bob joined", EventText.Describe(room!, joined));

        // Act: this thread in Finnish
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fi");
        try
        {
            // Done
            Assert.Equal("Bob liittyi", EventText.Describe(room!, joined));
        }
        finally
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        }
    }
}
