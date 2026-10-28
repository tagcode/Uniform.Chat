namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Platform;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;

/// <summary>Shows the current page of <see cref="ViewModels.MainViewModel"/>; on phones it makes room for the on-screen keyboard.</summary>
public partial class MainView : UserControl
{
    private IInputPane? inputPane;
    private Rect keyboard;

    /// <summary>Create the view.</summary>
    public MainView() => InitializeComponent();

    /// <summary>Follow the on-screen keyboard.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        // Android 15 draws apps edge to edge, so the window no longer shrinks for the keyboard
        base.OnLoaded(e);
        inputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (inputPane is null) return;
        inputPane.StateChanged += OnInputPaneChanged;
        UpdateKeyboardSpace(inputPane.OccludedRect);
    }

    /// <summary>Stop following the keyboard.</summary>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        // Unhook
        base.OnUnloaded(e);
        if (inputPane is not null) inputPane.StateChanged -= OnInputPaneChanged;
        inputPane = null;
    }

    /// <summary>Make room when the keyboard opens or closes.</summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e) => UpdateKeyboardSpace(e.EndRect);

    /// <summary>Measure again when the view's size changes, as older Android versions shrink the window for the keyboard only after it opened.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        // Same keyboard, new size
        base.OnSizeChanged(e);
        if (inputPane is not null) UpdateKeyboardSpace(keyboard);
    }

    /// <summary>Lift the page by the part of the view the keyboard covers, nothing when the window already shrank for it, as before Android 15, and scroll the focused field into view. The view's own padding stays for the system bars.</summary>
    private void UpdateKeyboardSpace(Rect occluded)
    {
        // Overlap of the keyboard with this view, in the view's coordinates
        keyboard = occluded;
        if (TopLevel.GetTopLevel(this) is not TopLevel top || this.TranslatePoint(new Point(0, Bounds.Height), top) is not Point bottom) return;
        double covered = occluded.Height > 0 ? Math.Max(0, bottom.Y - occluded.Top) : 0;
        PageHost.Margin = new Thickness(0, 0, 0, covered);
        // Done, the field being typed in scrolled into what is left, once laid out again
        if (occluded.Height > 0) Dispatcher.UIThread.Post(() => (top.FocusManager?.GetFocusedElement() as Control)?.BringIntoView(), DispatcherPriority.Background);
    }
}
