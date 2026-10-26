namespace Uniform.Matrix.Protocol.Crypto.Tests;

using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Test accounts from <c>testclient.txt</c> at the repository root, and their recovery keys in <c>testclient-recovery.txt</c>.</summary>
/// <remarks><c>testclient.txt</c> lists user IDs, one per line, and a <c>password: ...</c> line shared by all of them.</remarks>
internal static class LiveAccounts
{
    private const string AccountFile = "testclient.txt";
    private const string RecoveryFile = "testclient-recovery.txt";

    /// <summary>Directory holding <c>testclient.txt</c>, found upwards from the test binary; skip the test when there is none.</summary>
    private static string Root
    {
        get
        {
            // Search upwards
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, AccountFile))) directory = directory.Parent;
            // Live tests need the git-ignored account file
            Assert.SkipWhen(directory is null, $"{AccountFile} is missing; live tests need test accounts.");
            // Return directory
            return directory!.FullName;
        }
    }

    /// <summary>Return the user IDs in file order, and the password.</summary>
    public static (IReadOnlyList<string> UserIds, string Password) Load()
    {
        // Non-empty lines: user IDs and the password line
        string[] lines = [.. File.ReadAllLines(Path.Combine(Root, AccountFile)).Select(l => l.Trim()).Where(l => l.Length > 0)];
        string password = lines.First(l => l.StartsWith("password:"))["password:".Length..].Trim();
        // Return accounts
        return ([.. lines.Where(l => l.StartsWith('@'))], password);
    }

    /// <summary>Log in a new device of <paramref name="userId"/>.</summary>
    public static async Task<MatrixClient> LoginAsync(HttpClient http, string userId, string password, string deviceName, CancellationToken cancellationToken)
    {
        // Discover and log in
        Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, UserId.Parse(userId).ServerName, cancellationToken);
        MatrixClient client = new(http, homeserver);
        await client.LoginWithPasswordAsync(userId, password, deviceName, cancellationToken);
        // Return client
        return client;
    }

    /// <summary>Return the saved recovery key of <paramref name="userId"/>, or null.</summary>
    public static string? GetRecoveryKey(string userId)
    {
        // Lines of user ID, tab, key
        string path = Path.Combine(Root, RecoveryFile);
        if (!File.Exists(path)) return null;
        // Return key
        return File.ReadAllLines(path).Select(l => l.Split('\t', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == userId)?[1];
    }

    /// <summary>Save the recovery key of <paramref name="userId"/>, replacing an older one.</summary>
    public static void SaveRecoveryKey(string userId, string recoveryKey)
    {
        // Rewrite without the old line
        string path = Path.Combine(Root, RecoveryFile);
        List<string> lines = File.Exists(path) ? [.. File.ReadAllLines(path).Where(l => !l.StartsWith(userId + "\t"))] : [];
        lines.Add($"{userId}\t{recoveryKey}");
        File.WriteAllLines(path, lines);
    }
}
