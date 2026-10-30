namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Layout;
using global::Avalonia.Controls.Platform;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;

/// <summary>Shows the current page of <see cref="ViewModels.MainViewModel"/>; on phones it makes room for the on-screen keyboard.</summary>
public partial class MainView : UserControl
{
    /// <summary>The largest share of the view a keyboard can cover; more means a stale or wrong report.</summary>
    private const double MaxKeyboardShare = 0.8;
    /// <summary>How long Android's keyboard and activity animations take, after which the layout is run again.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);
    private IInputPane? inputPane;
    private IActivatableLifetime? activatable;

    /// <summary>Create the view.</summary>
    public MainView() => InitializeComponent();

    /// <summary>Follow the on-screen keyboard.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        // Android 15 draws apps edge to edge, so the window no longer shrinks for the keyboard
        base.OnLoaded(e);
        activatable = Application.Current?.TryGetFeature<IActivatableLifetime>();
        if (activatable is not null) activatable.Activated += OnActivated;
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
        if (activatable is not null) activatable.Activated -= OnActivated;
        activatable = null;
        if (inputPane is not null) inputPane.StateChanged -= OnInputPaneChanged;
        inputPane = null;
    }

    /// <summary>Make room when the keyboard opens or closes, and lay out again once it has.</summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        // Now, and after the animation
        UpdateKeyboardSpace(e.EndRect);
        RelayoutLater();
    }

    /// <summary>Lay out again when the app returns, e.g. from another app or the file picker.</summary>
    private void OnActivated(object? sender, ActivatedEventArgs e) => RelayoutLater();

    /// <summary>Measure and arrange the whole view again once Android has settled: after returning from another activity with the keyboard opening or closing, Android may leave parts arranged for a size it had only in between, until something moves.</summary>
    private void RelayoutLater() => DispatcherTimer.RunOnce(() =>
    {
        // Every layoutable, so that none keeps its stale arrangement
        if (inputPane is not null) UpdateKeyboardSpace(inputPane.State == InputPaneState.Open ? inputPane.OccludedRect : default);
        foreach (Layoutable layoutable in this.GetVisualDescendants().OfType<Layoutable>()) layoutable.InvalidateMeasure();
        InvalidateVisual();
    }, SettleTime);

    /// <summary>Measure again when the view's size changes, as older Android versions shrink the window for the keyboard only after it opened, and the app returns from the background with a new size.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        // The keyboard as it is now, not as it was: it may have closed while the app was in the background
        base.OnSizeChanged(e);
        if (inputPane is null) return;
        UpdateKeyboardSpace(inputPane.State == InputPaneState.Open ? inputPane.OccludedRect : default);
    }

    /// <summary>Lift the page by the part of the view the keyboard covers, nothing when the window already shrank for it, as before Android 15, and scroll the focused field into view. The view's own padding stays for the system bars.</summary>
    private void UpdateKeyboardSpace(Rect occluded)
    {
        // Overlap of the keyboard with this view, in the view's coordinates; none when it would cover nearly all
        if (TopLevel.GetTopLevel(this) is not TopLevel top || this.TranslatePoint(new Point(0, Bounds.Height), top) is not Point bottom) return;
        double covered = occluded.Height > 0 ? Math.Max(0, bottom.Y - occluded.Top) : 0;
        if (covered > Bounds.Height * MaxKeyboardShare) covered = 0;
        PageHost.Margin = new Thickness(0, 0, 0, covered);
        // Done, the field being typed in scrolled into what is left, once laid out again
        if (occluded.Height > 0) Dispatcher.UIThread.Post(() => (top.FocusManager?.GetFocusedElement() as Control)?.BringIntoView(), DispatcherPriority.Background);
    }
}
