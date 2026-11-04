using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocumentStorage.Core;

public sealed record StorageSnapshot(string Kind, string Hash, int Files, int Folders, long Bytes, string? Error = null, int Version = 1)
{
    public bool Readable => Kind is "Missing" or "File" or "Folder";
    public bool Exists => Kind is "File" or "Folder";
}

// A content/shape digest, independent of the root location and file timestamps.
// No document text or absolute paths are retained in the journal.
public static class StorageSnapshotter
{
    public const int MaxEntries = 10_000;
    public const long MaxBytes = 10L * 1024 * 1024 * 1024;
    public static async Task<StorageSnapshot> CaptureAsync(string root, string relative, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        try
        {
            var path = StoragePaths.Resolve(root, relative);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { return new("Missing", "", 0, 0, 0); }
            var folder = attributes.HasFlag(FileAttributes.Directory);
            var inventory = new List<Item>();
            long bytes = 0;
            void Visit(string suffix)
            {
                token.ThrowIfCancellationRequested();
                if (suffix.Count(c => c == '/') > 128) throw new StorageException("Verification is limited to 128 folder levels. Organize a shallower subtree first.");
                if (inventory.Count >= MaxEntries) throw new StorageException("Verification is limited to 10,000 entries per operation. Organize smaller subfolders first.");
                var current = StoragePaths.Resolve(root, suffix.Length == 0 ? relative : StoragePaths.Join(relative, suffix));
                var attrs = File.GetAttributes(current);
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) throw new StorageException("Verification refuses symbolic links and junctions, including those inside a folder.");
                var directory = attrs.HasFlag(FileAttributes.Directory);
                var length = directory ? 0 : new FileInfo(current).Length;
                bytes = checked(bytes + length);
                if (bytes > MaxBytes) throw new StorageException("Verification is limited to 10 GB per operation. Organize smaller subfolders first.");
                inventory.Add(new(suffix, directory, length, File.GetLastWriteTimeUtc(current).Ticks));
                if (directory)
                    foreach (var child in Directory.EnumerateFileSystemEntries(current))
                        Visit(suffix.Length == 0 ? Path.GetFileName(child) : StoragePaths.Join(suffix, Path.GetFileName(child)));
            }
            Visit("");
            var original = inventory.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
            using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string fileHash = "";
            foreach (var item in original)
            {
                token.ThrowIfCancellationRequested();
                var hash = "";
                if (!item.Folder)
                {
                    var current = StoragePaths.Resolve(root, item.Path.Length == 0 ? relative : StoragePaths.Join(relative, item.Path));
                    await using var stream = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
                    if (stream.Length != item.Bytes || File.GetLastWriteTimeUtc(current).Ticks != item.Modified)
                        throw new StorageException("Files changed during verification. Wait for other programs to finish and inspect again.");
                    fileHash = hash;
                }
                aggregate.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { item.Path, item.Folder, item.Bytes, Hash = hash }) + "\n"));
            }
            inventory.Clear(); bytes = 0; Visit("");
            if (!original.SequenceEqual(inventory.OrderBy(x => x.Path, StringComparer.Ordinal))) throw new StorageException("The folder changed during verification. Inspect again after other programs finish.");
            return new(folder ? "Folder" : "File", folder ? Convert.ToHexStringLower(aggregate.GetHashAndReset()) : fileHash,
                original.Count(x => !x.Folder), original.Count(x => x.Folder), bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new("Unavailable", "", 0, 0, 0, "Verification exceeded two minutes. Inspect a smaller operation or restore storage access."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException or OverflowException)
        { return new("Unavailable", "", 0, 0, 0, ex is StorageException ? ex.Message : "Storage could not be read completely. Check access, availability and other programs, then inspect again."); }
    }
    private sealed record Item(string Path, bool Folder, long Bytes, long Modified);
}
