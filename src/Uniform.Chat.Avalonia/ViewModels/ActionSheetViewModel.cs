namespace Uniform.Chat.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>An action of an <see cref="ActionSheetViewModel"/>: it runs, or offers <paramref name="Choices"/> such as report reasons.</summary>
/// <param name="Label">What the action says, e.g. <c>Reply</c>.</param>
/// <param name="Run">What it does, or null when it only offers choices.</param>
/// <param name="Choices">Actions to choose from instead, or null.</param>
/// <param name="IsDanger">Whether it removes or blocks something, and is shown in red.</param>
public sealed record SheetAction(string Label, Action? Run, IReadOnlyList<SheetAction>? Choices = null, bool IsDanger = false)
{
    /// <summary>Whether it offers choices instead of running.</summary>
    public bool HasChoices => Choices is { Count: > 0 };
}

/// <summary>A line of an <see cref="ActionSheetViewModel"/>'s details.</summary>
/// <param name="Label">What it is, e.g. <c>Sent</c>.</param>
/// <param name="Value">Its value.</param>
public sealed record SheetDetail(string Label, string Value);

/// <summary>The details and actions of a message or room, opened by a long press or a right click: on phones a sheet from the bottom, on desktops a menu whose details open the sheet.</summary>
public sealed partial class ActionSheetViewModel : ViewModelBase
{
    private readonly Action<string>? react;
    private readonly Action close;

    /// <summary>Create the sheet.</summary>
    /// <param name="title">What it is about, e.g. the room's name.</param>
    /// <param name="details">Information lines.</param>
    /// <param name="actions">Actions, in order.</param>
    /// <param name="reactions">Quick reactions to offer in a row; empty for none.</param>
    /// <param name="react">Sends a reaction, or null.</param>
    /// <param name="close">Closes the sheet.</param>
    public ActionSheetViewModel(string title, IReadOnlyList<SheetDetail> details, IReadOnlyList<SheetAction> actions, IReadOnlyList<string> reactions, Action<string>? react, Action close)
    {
        // Top level shown first
        Title = title;
        Details = details;
        Actions = actions;
        Reactions = reactions;
        this.react = react;
        this.close = close;
        Shown = actions;
    }

    /// <summary>What the sheet is about.</summary>
    public string Title { get; }

    /// <summary>Information lines.</summary>
    public IReadOnlyList<SheetDetail> Details { get; }

    /// <summary>Whether there are information lines.</summary>
    public bool HasDetails => Details.Count > 0;

    /// <summary>All top-level actions.</summary>
    public IReadOnlyList<SheetAction> Actions { get; }

    /// <summary>Quick reactions.</summary>
    public IReadOnlyList<string> Reactions { get; }

    /// <summary>Whether quick reactions are offered.</summary>
    public bool HasReactions => Reactions.Count > 0 && react is not null;

    /// <summary>The actions shown now: the top level, or the choices of one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing))]
    public partial IReadOnlyList<SheetAction> Shown { get; set; }

    /// <summary>The action whose choices are shown, e.g. <c>Report message</c>, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing))]
    public partial string? Choosing { get; set; }

    /// <summary>Whether the choices of an action are shown instead of the top level.</summary>
    public bool IsChoosing => Choosing is not null;

    /// <summary>Show the choices of <paramref name="action"/>, or close the sheet and run it.</summary>
    [RelayCommand]
    private void Choose(SheetAction action)
    {
        // Choices first
        if (action.Choices is { Count: > 0 } choices)
        {
            Choosing = action.Label;
            Shown = choices;
            return;
        }
        close();
        // Done
        action.Run?.Invoke();
    }

    /// <summary>Close the sheet and send the reaction <paramref name="key"/>.</summary>
    [RelayCommand]
    private void React(string key)
    {
        // Closed first, so the reaction shows on the message
        close();
        react?.Invoke(key);
    }

    /// <summary>Go back from choices to the top level; return false when the top level is shown already.</summary>
    public bool GoBack()
    {
        // Choices first
        if (Choosing is null) return false;
        Choosing = null;
        Shown = Actions;
        // Done
        return true;
    }

    /// <summary>Go back from choices to the top level.</summary>
    [RelayCommand]
    private void Back() => GoBack();

    /// <summary>Close the sheet without doing anything.</summary>
    [RelayCommand]
    private void Close() => close();
}
