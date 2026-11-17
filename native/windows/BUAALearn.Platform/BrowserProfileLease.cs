namespace BUAALearn.Platform;

/// Per-surface temporary profiles with a live lease. Browser processes may delay
/// deletion; the next launch retries only generated, abandoned directories.
public sealed class BrowserProfileLease : IDisposable
{
    public string Path { get; }
    private readonly FileStream lease;
    private int disposed;
    private BrowserProfileLease(string path, FileStream lease) { Path = path; this.lease = lease; }
    public static BrowserProfileLease Create(WorkspacePaths paths, string purpose)
    {
        if (purpose is not ("workspace" or "school")) throw new ArgumentException("浏览器资料用途无效。");
        var root = System.IO.Path.Combine(paths.Cache, "browser-" + purpose);
        WorkspacePaths.SecureDirectory(root);
        foreach (var old in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(System.IO.Path.GetFileName(old), "N", out _) || (File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var lockPath = System.IO.Path.Combine(old, "profile.lock"); WorkspacePaths.RejectLink(lockPath);
                using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                TryDelete(old);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")); WorkspacePaths.SecureDirectory(path);
        var file = System.IO.Path.Combine(path, "profile.lock");
        var lease = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { WorkspacePaths.SecureFile(file); return new(path, lease); }
        catch { lease.Dispose(); TryDelete(path); throw; }
    }
    private static void TryDelete(string path)
    {
        try { WorkspacePaths.RejectLink(path); Directory.Delete(path, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lease.Dispose(); TryDelete(Path);
    }
}
