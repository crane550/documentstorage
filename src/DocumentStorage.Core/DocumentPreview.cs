using System.Security.Cryptography;
using System.Text;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

public sealed record PreviewFormat(string Kind, string Mime, long Limit);
public sealed record PreviewText(string Text, bool Truncated);
public sealed record PreviewFile(FileStream Stream, string Mime, string Name);

public static class DocumentPreview
{
    public const int TextCharacters = 200_000;
    public static PreviewFormat Format(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => new("pdf", "application/pdf", 250L * 1024 * 1024),
        ".png" => new("image", "image/png", 32L * 1024 * 1024),
        ".jpg" or ".jpeg" => new("image", "image/jpeg", 32L * 1024 * 1024),
        ".gif" => new("image", "image/gif", 32L * 1024 * 1024),
        ".webp" => new("image", "image/webp", 32L * 1024 * 1024),
        ".bmp" => new("image", "image/bmp", 32L * 1024 * 1024),
        ".avif" => new("image", "image/avif", 32L * 1024 * 1024),
        ".txt" or ".md" or ".csv" or ".tsv" or ".json" or ".xml" or ".yaml" or ".yml" or ".log" or ".ini" or ".html" or ".htm" or ".svg" => new("text", "text/plain", long.MaxValue),
        _ => new("unsupported", "application/octet-stream", 0)
    };
    public static string Revision(CatalogEntry entry) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{entry.Id}|{entry.RootId}|{entry.RelativePath}|{entry.Size}|{entry.ModifiedAt.UtcTicks}")));
    internal static bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => mime switch
    {
        "application/pdf" => header.IndexOf("%PDF-"u8) is >= 0 and < 1024,
        "image/png" => header.StartsWith(new byte[] {137,80,78,71,13,10,26,10}),
        "image/jpeg" => header.StartsWith(new byte[] {255,216,255}),
        "image/gif" => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8),
        "image/webp" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8,4).SequenceEqual("WEBP"u8),
        "image/bmp" => header.StartsWith("BM"u8),
        "image/avif" => AvifHeader(header),
        _ => false
    };
    private static bool AvifHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header.Slice(4,4).SequenceEqual("ftyp"u8)) return false;
        var boxLength = Math.Min((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[..4]),header.Length);
        for (var offset=8;offset+4<=boxLength;offset+=offset==8?8:4)
            if (header.Slice(offset,4).SequenceEqual("avif"u8) || header.Slice(offset,4).SequenceEqual("avis"u8)) return true;
        return false;
    }
}

public sealed partial class LibraryService
{
    public async Task<PreviewFile> OpenPreviewAsync(Guid id, string revision, bool text = false)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            if (entry == null || entry.IsFolder || entry.IsMissing || entry.TrashBatchId != null) throw new StorageException("This document is unavailable. Refresh the library and select it again.");
            if (DocumentPreview.Revision(entry) != revision) throw new StorageException("This document changed. Refresh the library and reload its preview.");
            var format = DocumentPreview.Format(entry.Name);
            if (format.Kind == "unsupported" || (format.Kind == "text") != text) throw new StorageException("This file type has no preview. Download the original to open it.");
            var root = await db.Roots.SingleAsync(x => x.Id == entry.RootId); await RequireOnlineAsync(root);
            var path = StoragePaths.Resolve(root.Location, entry.RelativePath);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                if (stream.Length != entry.Size || FileTimestamp(File.GetLastWriteTimeUtc(path)) != entry.ModifiedAt) throw new StorageException("The file changed on storage. Rescan it and reload the preview.");
                if (stream.Length > format.Limit) throw new StorageException("This file is too large for an inline preview. Download the original to open it.");
                if (!text)
                {
                    var header = new byte[1032]; var length = await stream.ReadAtLeastAsync(header, (int)Math.Min(stream.Length, header.Length), throwOnEndOfStream: false);
                    if (!DocumentPreview.HeaderMatches(format.Mime, header.AsSpan(0,length))) throw new StorageException("The file contents do not match its extension. Download the original to inspect it.");
                    stream.Position = 0;
                }
                return new(stream, format.Mime, entry.Name);
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        finally { writer.Release(); }
    }
    public async Task<PreviewText> ReadPreviewTextAsync(Guid id, string revision)
    {
        var file = await OpenPreviewAsync(id, revision, text: true);
        await using var stream = file.Stream;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[DocumentPreview.TextCharacters + 1];
        try
        {
            var length = await reader.ReadBlockAsync(buffer.AsMemory());
            if (buffer.AsSpan(0,length).Contains('\0')) throw new StorageException("This file contains binary data and cannot be shown as text.");
            return new(new string(buffer,0,Math.Min(length,DocumentPreview.TextCharacters)), length > DocumentPreview.TextCharacters);
        }
        catch (DecoderFallbackException) { throw new StorageException("The text encoding could not be read. Download the original to open it."); }
    }
}
