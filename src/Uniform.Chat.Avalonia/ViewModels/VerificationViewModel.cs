namespace Uniform.Chat.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Chat.Avalonia.Services;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>An emoji verification with another device as it goes: accept a request, compare emoji, then the outcome.</summary>
public sealed partial class VerificationViewModel : ViewModelBase
{
    private readonly ClientConnection connection;
    private readonly Action close;

    /// <summary>Follow <paramref name="verification"/>; <paramref name="close"/> hides the panel.</summary>
    public VerificationViewModel(ClientConnection connection, Verification verification, Action close)
    {
        // Show the current state
        this.connection = connection;
        this.close = close;
        Verification = verification;
        Update(verification.State);
    }

    /// <summary>The engine's verification.</summary>
    public Verification Verification { get; }

    /// <summary>What is happening, e.g. <c>Compare the emoji…</c>.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>Whether the other side asks and waits for an answer.</summary>
    [ObservableProperty]
    public partial bool IsIncoming { get; set; }

    /// <summary>Whether the emoji are shown for comparing.</summary>
    [ObservableProperty]
    public partial bool IsComparing { get; set; }

    /// <summary>Whether it ended, verified or not.</summary>
    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    /// <summary>Emoji to compare, in order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SasEmoji> Emoji { get; set; } = [];

    /// <summary>The same as numbers, for devices without emoji.</summary>
    [ObservableProperty]
    public partial string Numbers { get; set; } = "";

    /// <summary>Show <paramref name="state"/>.</summary>
    public void Update(VerificationState state)
    {
        // The other device is known once it answered
        string who = Verification.OtherUserId == connection.UserId ? $"your device {Verification.OtherDeviceId}".TrimEnd() : $"{Verification.OtherUserId} {Verification.OtherDeviceId}".TrimEnd();
        IsIncoming = state == VerificationState.Requested && !Verification.WeRequested;
        IsComparing = state == VerificationState.ShowingSas;
        IsFinished = state is VerificationState.Done or VerificationState.Cancelled;
        Emoji = IsComparing ? Verification.Emoji : [];
        Numbers = IsComparing ? string.Join("  ", Verification.Decimals) : "";
        // Return message by state
        Message = state switch
        {
            VerificationState.Requested when IsIncoming => $"{who} wants to verify this device by comparing emoji.",
            VerificationState.Requested => $"Accept the request on {who}.",
            VerificationState.Ready => $"Starting with {who}…",
            VerificationState.ShowingSas => $"Compare with {who}. Do they show the same emoji in the same order?",
            VerificationState.Confirmed => $"Waiting for {who} to confirm…",
            VerificationState.Done => $"{char.ToUpperInvariant(who[0])}{who[1..]} is verified.",
            _ => Verification.CancelCode is "m.user" or "m.accepted" ? "Verification cancelled." : $"Verification cancelled: {Verification.CancelReason}",
        };
    }

    /// <summary>Accept the incoming request.</summary>
    [RelayCommand]
    private Task AcceptAsync() => RunAsync(() => connection.Crypto.AcceptVerificationAsync(Verification));

    /// <summary>The emoji match: trust the other device.</summary>
    [RelayCommand]
    private Task MatchAsync() => RunAsync(() => connection.Crypto.ConfirmVerificationAsync(Verification));

    /// <summary>The emoji differ: stop, as someone may be in between.</summary>
    [RelayCommand]
    private Task MismatchAsync() => RunAsync(() => connection.Crypto.CancelVerificationAsync(Verification, "m.mismatched_sas"));

    /// <summary>Decline or stop the verification and close the panel.</summary>
    [RelayCommand]
    private async Task CancelAsync()
    {
        // Finished ones only close
        if (!IsFinished) await RunAsync(() => connection.Crypto.CancelVerificationAsync(Verification));
        close();
    }

    /// <summary>Close the finished verification.</summary>
    [RelayCommand]
    private void Close() => close();

    /// <summary>Run a step; failures show as the message.</summary>
    private async Task RunAsync(Func<Task> step)
    {
        // The next state arrives through the engine's event
        try
        {
            await step();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // On error tell
            Message = $"Verification failed: {exception.Message}";
        }
    }
}
