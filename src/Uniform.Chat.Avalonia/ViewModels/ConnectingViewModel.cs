namespace Uniform.Chat.Avalonia.ViewModels;

using Uniform.Chat.Avalonia.Localization;

/// <summary>Shown while the remembered account resumes its session.</summary>
public sealed class ConnectingViewModel(string userId) : ViewModelBase
{
    /// <summary>Account being resumed.</summary>
    public string UserId { get; } = userId;

    /// <summary>What the screen says while signing in.</summary>
    public string SigningInText => Translations.Format(Strings.SigningInTo, UserId);
}
