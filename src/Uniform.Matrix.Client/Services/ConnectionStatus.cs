namespace Uniform.Matrix.Client.Services;

/// <summary>Connection state shown by the UI.</summary>
public enum ConnectionState
{
    /// <summary>Preparing storage or logging in.</summary>
    Connecting,
    /// <summary>Synced and following updates.</summary>
    Connected,
    /// <summary>Sync failed transiently and will be retried.</summary>
    Retrying,
    /// <summary>Stopped by an error that retrying cannot fix, e.g. a rejected login.</summary>
    Failed,
}

/// <summary>Current connection state, published by <see cref="MatrixWorker"/>; <see cref="Changed"/> fires on the worker's thread.</summary>
public sealed class ConnectionStatus
{
    private readonly Lock gate = new();

    /// <summary>Current state.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Connecting;
    /// <summary>Detail of the state, e.g. the last error.</summary>
    public string? Message { get; private set; }
    /// <summary>Raised after every change.</summary>
    public event Action<ConnectionState, string?>? Changed;

    /// <summary>Set the state and notifies listeners.</summary>
    public void Set(ConnectionState state, string? message = null)
    {
        // Update under lock
        lock (gate) (State, Message) = (state, message);
        // Notify
        Changed?.Invoke(state, message);
    }
}
