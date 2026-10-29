namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Reflection;

/// <summary>A crypto store that can lose its Olm sessions, as a device whose state went back or got corrupted does: <see cref="ForgetOlmSessions"/> hides the sessions there are now, and later ones stay.</summary>
internal class ForgetfulStore : DispatchProxy
{
    private readonly HashSet<string> forgotten = [];
    private ICryptoStore inner = null!;

    /// <summary>Return a store over <paramref name="store"/>, and its control.</summary>
    public static (ICryptoStore Store, ForgetfulStore Control) Over(ICryptoStore store)
    {
        // Proxy of the interface, wrapping the store
        ICryptoStore proxy = Create<ICryptoStore, ForgetfulStore>();
        ForgetfulStore control = (ForgetfulStore)(object)proxy;
        control.inner = store;
        // Return both
        return (proxy, control);
    }

    /// <summary>Hide the Olm sessions stored with <paramref name="senderKey"/> so far.</summary>
    public async Task ForgetOlmSessions(string senderKey)
    {
        // By session ID
        foreach (OlmSessionRecord record in await inner.LoadOlmSessionsAsync(senderKey, CancellationToken.None)) forgotten.Add(record.SessionId);
    }

    /// <summary>Pass calls on, without the forgotten sessions.</summary>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        // Loading sessions filters; the rest passes
        object? result = targetMethod!.Invoke(inner, args);
        if (targetMethod.Name != nameof(ICryptoStore.LoadOlmSessionsAsync)) return result;
        IReadOnlyList<OlmSessionRecord> records = ((Task<IReadOnlyList<OlmSessionRecord>>)result!).Result;
        // Return what is left
        return Task.FromResult<IReadOnlyList<OlmSessionRecord>>([.. records.Where(r => !forgotten.Contains(r.SessionId))]);
    }
}
