namespace Uniform.Matrix.Server.Storage;

using System.Globalization;

/// <summary>Keeps other server processes off a data directory while this one uses it: <see cref="LockFileName"/> is held open exclusively, which on Linux is an <c>flock</c> that the kernel releases when the process ends, also when it is killed, so the lock never goes stale.</summary>
/// <remarks><see cref="ProcessFileName"/> next to it tells which process holds it, for the message of the next one; it may be left behind, as only the lock counts.</remarks>
public sealed class DataDirectoryLock : IDisposable
{
    /// <summary>Name of the lock file in the data directory.</summary>
    public const string LockFileName = "server.lock";

    /// <summary>Name of the file with the holder's process ID.</summary>
    public const string ProcessFileName = "server.pid";

    private readonly FileStream file;

    /// <summary>Hold <paramref name="file"/> for <paramref name="directory"/>.</summary>
    private DataDirectoryLock(string directory, FileStream file)
    {
        // Store parts
        Directory = directory;
        this.file = file;
    }

    /// <summary>The locked data directory.</summary>
    public string Directory { get; }

    /// <summary>Lock <paramref name="directory"/> for this process, creating it when missing; throw <see cref="DataDirectoryInUseException"/> when another process has it.</summary>
    public static DataDirectoryLock Acquire(string directory)
    {
        // Exclusive open of the lock file, which fails while another process holds it
        System.IO.Directory.CreateDirectory(directory);
        FileStream file;
        try
        {
            file = new FileStream(Path.Combine(directory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new DataDirectoryInUseException(directory, ReadProcessId(directory), ex);
        }

        // Tell who holds it
        File.WriteAllText(Path.Combine(directory, ProcessFileName), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        // Return lock
        return new DataDirectoryLock(directory, file);
    }

    /// <summary>Release the lock.</summary>
    public void Dispose() => file.Dispose();

    /// <summary>Return the process ID in <see cref="ProcessFileName"/>, or null when unreadable.</summary>
    private static int? ReadProcessId(string directory)
    {
        // The holder wrote it after locking
        try
        {
            return int.TryParse(File.ReadAllText(Path.Combine(directory, ProcessFileName)).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // On an unreadable file
            return null;
        }
    }
}

/// <summary>Another server process uses the data directory; stop it first.</summary>
/// <param name="directory">The data directory.</param>
/// <param name="processId">The other process, when known.</param>
/// <param name="inner">The failed open of the lock file.</param>
public sealed class DataDirectoryInUseException(string directory, int? processId, Exception? inner = null)
    : IOException($"Another server{(processId is int id ? $" (pid {id})" : "")} is using {directory}; stop it first.", inner)
{
    /// <summary>The data directory.</summary>
    public string Directory { get; } = directory;

    /// <summary>The other process, when known.</summary>
    public int? ProcessId { get; } = processId;
}
