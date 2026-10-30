namespace Uniform.Chat.Avalonia.Services;

using System.Text.Json.Nodes;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Hosting;

/// <summary>The last signed-in account, kept in the app's <c>settings.json</c> as <c>Matrix:UserId</c> and <c>Matrix:Homeserver</c>, so the next start resumes it.</summary>
public sealed class AccountMemory
{
    private readonly string path;

    /// <summary>Use the settings file of <paramref name="applicationName"/>.</summary>
    public AccountMemory(string applicationName)
    {
        // Same file the settings window of the engine uses
        path = SettingsStore.DefaultPath(applicationName);
    }

    /// <summary>Return the remembered account without a password, or null.</summary>
    public SignIn? Load()
    {
        // Flat keys of the settings file
        IReadOnlyDictionary<string, string?> values;
        try
        {
            values = SettingsStore.Read(path);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
        {
            return null;
        }
        string? userId = values.GetValueOrDefault($"{MatrixOptions.Section}:{nameof(MatrixOptions.UserId)}");
        // Return account
        return string.IsNullOrWhiteSpace(userId) ? null : new SignIn { UserId = userId, Homeserver = values.GetValueOrDefault($"{MatrixOptions.Section}:{nameof(MatrixOptions.Homeserver)}") };
    }

    /// <summary>Remember the account of a successful sign-in; the password is never saved.</summary>
    public void Save(SignIn signIn)
    {
        // Merge into the settings file
        JsonObject root = SettingsFile.Read(path);
        JsonObject matrix = SettingsFile.Section(root, MatrixOptions.Section);
        matrix[nameof(MatrixOptions.UserId)] = signIn.UserId;
        if (string.IsNullOrWhiteSpace(signIn.Homeserver)) matrix.Remove(nameof(MatrixOptions.Homeserver));
        else matrix[nameof(MatrixOptions.Homeserver)] = signIn.Homeserver;
        SettingsFile.Write(path, root);
    }

    /// <summary>Forget the account, e.g. after signing out.</summary>
    public void Forget()
    {
        // Remove the user ID, keep other settings
        JsonObject root = SettingsFile.Read(path);
        if (root[MatrixOptions.Section] is not JsonObject matrix || !matrix.Remove(nameof(MatrixOptions.UserId))) return;
        SettingsFile.Write(path, root);
    }
}
