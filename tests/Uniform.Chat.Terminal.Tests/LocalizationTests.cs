namespace Uniform.Chat.Terminal.Tests;

using System.Reflection;
using System.Resources;
using Uniform.Matrix.Testing;

/// <summary>The texts of the terminal client and of its windows and dialogs: every one in each language but the right-to-left ones, which the terminal shows in English, and different hotkeys in each menu.</summary>
public class LocalizationTests
{
    /// <summary>The texts of the client.</summary>
    private static readonly ResourceManager Client = new("Uniform.Chat.Terminal.Localization.Strings", Assembly.Load("Uniform.Chat"));

    /// <summary>The texts of the windows and dialogs.</summary>
    private static readonly ResourceManager Windows = new("Uniform.Matrix.Gui.Terminal.Localization.Strings", Assembly.Load("Uniform.Matrix.Gui.Terminal"));

    /// <summary>The texts shown together, whose hotkeys must differ: the menu bar, each menu, and the dialogs with several buttons.</summary>
    private static readonly string[][] ClientGroups =
    [
        ["MenuFile", "MenuRoom", "MenuView", "MenuServer", "MenuHelp"],
        ["MenuEncryption", "MenuVerifyDevice", "MenuDevices", "MenuSettings", "MenuLogOut", "MenuDeleteAccount", "MenuQuit"],
        ["MenuJoin", "MenuDirectory", "MenuNewRoom", "MenuNewPublicRoom", "MenuDirectMessage", "MenuSendFile", "MenuCall", "MenuHangUp", "MenuInvite", "MenuSetTopic", "MenuSetName", "MenuSetAvatar", "MenuAddAlias", "MenuDirectoryListing", "MenuPowerLevel", "MenuKick", "MenuBan", "MenuUnban", "MenuEnableEncryption", "MenuLeave"],
        ["MenuOlderMessages", "MenuMembers", "MenuSelectMessage", "MenuLogs", "MenuAudioTest", "MenuNextRoom", "MenuPreviousRoom"],
        ["MenuAccounts", "MenuRegistrationTokens", "MenuReports", "MenuServerRooms", "MenuServerNotice", "MenuWhois"],
        ["MenuCommandsAndKeys", "MenuAbout"],
        ["ButtonQuit", "ButtonCreateAccount", "ButtonSignIn"],
        ["ButtonReply", "ButtonReact", "ButtonEdit", "ButtonViewImage", "ButtonSaveFile", "ButtonOpenLink", "ButtonDelete", "ButtonReport", "ButtonBlockSender", "ButtonClose"],
        ["ButtonClose", "ButtonRename", "ButtonLogOutDevice", "ButtonLogOutOthers", "ButtonVerifySelected"],
        ["ButtonClose", "ButtonSetUpRecovery", "ButtonRecover", "ButtonVerifyDevice"],
        ["ButtonTheyMatch", "ButtonTheyDontMatch", "ButtonCancel"],
        ["ButtonCancel", "ButtonShutDown", "ButtonShutDownBlock"],
        ["ButtonList", "ButtonUnlist", "ButtonClose"],
        ["ButtonLater", "ButtonReject", "ButtonJoin"],
        ["ReasonSpam", "ReasonHarassment", "ReasonSexualViolent", "ReasonIllegal", "ReasonOther", "ButtonCancel"],
    ];

    /// <summary>The window texts shown together.</summary>
    private static readonly string[][] WindowGroups =
    [
        ["ButtonEdit", "ButtonReset", "ButtonApply", "ButtonSave", "ButtonLoad", "ButtonExport", "ButtonClose"],
        ["FilterLabel", "LevelButton", "ButtonClose"],
        ["ButtonNo", "ButtonYes"],
        ["ButtonCancel", "ButtonOk"],
    ];

    /// <summary>Return every culture the terminal's texts are translated to.</summary>
    public static TheoryData<string> Cultures => LocalizationChecks.Cultures(rightToLeft: false);

    /// <summary>Every English text of the client is in <paramref name="culture"/>, in each plural form the language uses, with the same placeholders, and the buttons keep their hotkeys.</summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void TranslateEveryText(string culture) => LocalizationChecks.AssertTranslated(Client, culture, hotkeys: true);

    /// <summary>Every English text of the windows and dialogs is in <paramref name="culture"/>, as for the client.</summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void TranslateEveryWindowText(string culture) => LocalizationChecks.AssertTranslated(Windows, culture, hotkeys: true);

    /// <summary>The menus and dialogs have different hotkeys in <paramref name="culture"/>, and in English.</summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    [InlineData("en")]
    public void UseDistinctHotkeys(string culture)
    {
        // The client's, then the windows'
        LocalizationChecks.AssertDistinctHotkeys(Client, culture, ClientGroups);
        // Done
        LocalizationChecks.AssertDistinctHotkeys(Windows, culture, WindowGroups);
    }
}
