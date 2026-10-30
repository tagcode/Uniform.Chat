namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

/// <summary>Shown while the remembered account resumes its session.</summary>
public sealed class ConnectingViewModel(string userId) : ViewModelBase
{
    /// <summary>Account being resumed.</summary>
    public string UserId { get; } = userId;
}
