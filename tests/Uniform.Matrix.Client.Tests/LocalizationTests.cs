namespace Uniform.Matrix.Client.Tests;

using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Core;

/// <summary>The library's texts, such as event descriptions: every one in Finnish too, and shown in the current language.</summary>
public partial class LocalizationTests
{
    private static readonly ResourceManager Texts = new("Uniform.Matrix.Client.Localization.Strings", typeof(MatrixWorker).Assembly);

    /// <summary>Every English text has a Finnish one, not empty, with the same placeholders, and Finnish has no texts English lacks.</summary>
    [Fact]
    public void TranslateEveryText()
    {
        // Each culture's own texts
        Dictionary<string, string> english = Read(CultureInfo.InvariantCulture), finnish = Read(CultureInfo.GetCultureInfo("fi"));
        Assert.Empty(english.Keys.Except(finnish.Keys).Order());
        Assert.Empty(finnish.Keys.Except(english.Keys).Order());
        // Done: filled in, same placeholders
        foreach ((string key, string text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(finnish[key]), key);
            Assert.True(Placeholders(text).SetEquals(Placeholders(finnish[key])), $"{key}: other placeholders");
        }
    }

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

    /// <summary>Return one culture's texts, without its parents'.</summary>
    private static Dictionary<string, string> Read(CultureInfo culture) =>
        (Texts.GetResourceSet(culture, createIfNotExists: true, tryParents: false) ?? throw new InvalidOperationException(culture.Name)).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    /// <summary>Return the numbers of a text's placeholders.</summary>
    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    /// <summary>A placeholder of string.Format.</summary>
    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
