using System.Security.Cryptography;
using System.Text;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
using DocumentStorage.Core.Formats;
using System.Text.Json;

namespace DocumentStorage.Core;

public sealed record PreviewFormat(string Kind, string Mime, long Limit);
public sealed record PreviewText(string Text, bool Truncated);
public sealed record PreviewFile(FileStream Stream, string Mime, string Name);

public static class DocumentPreview
{
    public const int TextCharacters = 200_000;
    public static PreviewFormat Format(string name) => Formats.DocumentFormatRegistry.Default.Preview(name);
    public static string Revision(CatalogEntry entry) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{entry.Id}|{entry.RootId}|{entry.RelativePath}|{entry.Size}|{entry.ModifiedAt.UtcTicks}")));
    internal static bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => Formats.DocumentFormatRegistry.Default.HeaderMatches(mime, header);

}

public sealed partial class LibraryService
{
    public async Task<PreviewFile> OpenPreviewAsync(Guid id, string revision, bool text = false, bool derived = false)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            if (entry == null || entry.IsFolder || entry.IsMissing || entry.TrashBatchId != null) throw new StorageException("This document is unavailable. Refresh the library and select it again.");
            if (DocumentPreview.Revision(entry) != revision) throw new StorageException("This document changed. Refresh the library and reload its preview.");
            var format = textReader.Formats.Preview(entry.Name);
            if (format.Kind == "unsupported" || (format.Kind == "text") != text || (format.Kind is "document" or "spreadsheet") != derived) throw new StorageException("This file type has no preview. Download the original to open it.");
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
                    if (!textReader.Formats.HeaderMatches(format.Mime, header.AsSpan(0,length))) throw new StorageException("The file contents do not match its extension. Download the original to inspect it.");
                    stream.Position = 0;
                }
                return new(stream, format.Mime, entry.Name);
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        finally { writer.Release(); }
    }
    public async Task<DerivedPreview> ReadDerivedPreviewAsync(Guid id, string revision)
    {
        var file = await OpenPreviewAsync(id, revision, derived: true);
        await using var stream = file.Stream;
        await using var db = await factory.CreateDbContextAsync();
        var entry = await db.Entries.AsNoTracking().SingleAsync(x => x.Id == id);
        if (entry.TextStatus is "Text queued" or "Extracting") throw new StorageException("preview_preparing: Text extraction is preparing this preview.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        if (DocumentPreview.Revision(entry) != revision || entry.IsMissing || entry.TrashBatchId != null || hash != entry.ExtractionSourceHash || entry.ExtractionVersion != textReader.Formats.VersionFor(entry.Name) || entry.TextStatus != "Indexed")
            throw new StorageException("The text preview is not ready for these file contents. Wait for extraction, then reload the preview.");
        var text = entry.ExtractedText;
        var artifact = entry.ExtractionArtifactJson.Length == 0 ? null : JsonSerializer.Deserialize<DocumentArtifact>(entry.ExtractionArtifactJson);
        return new(text[..Math.Min(text.Length, DocumentPreview.TextCharacters)], entry.ExtractionTruncated || text.Length > DocumentPreview.TextCharacters || artifact?.Truncated == true, artifact);
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
