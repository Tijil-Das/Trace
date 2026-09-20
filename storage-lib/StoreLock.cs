using System.Text;

namespace ScreenRecall.Storage;

/// <summary>
/// Exclusive lock on a store root, held by whichever process is recording into it. Two capture
/// processes writing the same root would interleave appends in the shared log — each handle writes at
/// its own position — so the store is protected at the file-system level rather than trusted to behave.
/// Readers (the player, the dashboard) never take this lock; they only need shared read access.
/// </summary>
public sealed class StoreLock : IDisposable
{
    /// <summary>Name of the lock file inside a store root.</summary>
    public const string FileName = ".screenrecall-lock";

    private readonly FileStream _stream;
    private bool _disposed;

    private StoreLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>Path of the lock file.</summary>
    public string Path { get; }

    /// <summary>
    /// Takes the lock, or reports who holds it. Fails rather than proceeding: recording into a root
    /// that another process is already writing would corrupt the shared log.
    /// </summary>
    public static bool TryAcquire(string root, out StoreLock? acquired, out string? holder)
    {
        acquired = null;
        holder = null;
        string path = System.IO.Path.Combine(root, FileName);

        try
        {
            Directory.CreateDirectory(root);
            FileStream stream = new(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                128,
                FileOptions.WriteThrough);

            stream.SetLength(0);
            byte[] info = Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId} machine={Environment.MachineName} started={DateTimeOffset.Now:o}{Environment.NewLine}");
            stream.Write(info);
            stream.Flush();
            acquired = new StoreLock(path, stream);
            return true;
        }
        catch (IOException)
        {
            holder = TryDescribeHolder(path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            holder = $"'{path}' is not writable";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    /// <summary>Best-effort description of the process holding the lock.</summary>
    public static string? TryDescribeHolder(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            string text = reader.ReadToEnd().Trim();
            return text.Length > 0 ? text : "another process";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "another process";
        }
    }
}
