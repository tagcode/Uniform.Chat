namespace Uniform.Matrix.Client.Avalonia.Core.Services;

using global::Avalonia.Threading;

/// <summary>Run work on the user interface thread; the engine reports changes on background threads.</summary>
public interface IUiDispatcher
{
    /// <summary>Queue <paramref name="action"/> to run on the user interface thread.</summary>
    void Post(Action action);
}

/// <summary>Dispatcher of the running Avalonia application.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <summary>Queue <paramref name="action"/> on Avalonia's UI thread.</summary>
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
