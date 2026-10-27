namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Platform;
using global::Avalonia.Interactivity;

/// <summary>Shows the current page of <see cref="ViewModels.MainViewModel"/>; on phones it makes room for the on-screen keyboard.</summary>
public partial class MainView : UserControl
{
    private IInputPane? inputPane;

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

    /// <summary>Lift the page by the part of the view the keyboard covers; nothing when the window already shrank for it. The view's own padding stays for the system bars.</summary>
    private void UpdateKeyboardSpace(Rect keyboard)
    {
        // Overlap of the keyboard with this view, in the view's coordinates
        if (TopLevel.GetTopLevel(this) is not TopLevel top || this.TranslatePoint(new Point(0, Bounds.Height), top) is not Point bottom) return;
        double covered = keyboard.Height > 0 ? Math.Max(0, bottom.Y - keyboard.Top) : 0;
        // Done
        PageHost.Margin = new Thickness(0, 0, 0, covered);
    }
}
