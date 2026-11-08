using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentStorage.Core.Imaging;

namespace DocumentStorage.Core.Formats;

public sealed class ImageModule(TesseractOcr? engine = null) : IDocumentFormatModule, IRasterPreviewModule
{
    private readonly TesseractOcr ocr = engine ?? new();
    public IReadOnlyList<DocumentFormat> Formats { get; } = Definitions(engine ?? new());
    private static IReadOnlyList<DocumentFormat> Definitions(TesseractOcr engine)
    {
        var version = "2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(engine.Language)))[..8];
        DocumentFormat Image(string id, string name, string[] extensions, string mime, bool converted = false, string note = "")
            => new(id, version, name, extensions, true, converted ? "raster" : "image", mime, RasterImages.MaximumBytes,
                "Local Tesseract OCR. " + note);
        return [
            Image("png", "PNG image", [".png"], "image/png"),
            Image("jpeg", "JPEG image", [".jpg", ".jpeg", ".jpe", ".jfif"], "image/jpeg"),
            Image("bmp", "BMP image", [".bmp"], "image/bmp"),
            Image("dib", "DIB bitmap", [".dib"], "image/x-dib", true),
            Image("gif", "GIF image", [".gif"], "image/gif", note: "OCR uses the first animation frame."),
            Image("webp", "WebP image", [".webp"], "image/webp", note: "OCR uses the first animation frame."),
            Image("avif", "AVIF image", [".avif"], "image/avif"),
            Image("tiff", "TIFF document", [".tif", ".tiff"], "image/tiff", true, "Multipage preview and OCR, up to 20 pages."),
            Image("heic", "HEIC / HEIF image", [".heic", ".heif"], "image/heic", true, "Preview and OCR use the primary frame."),
            Image("ico", "Windows icon", [".ico"], "image/x-icon", true, "Preview and OCR use the first icon size."),
            Image("pbm", "Portable bitmap", [".pbm"], "image/x-portable-bitmap", true),
            Image("pgm", "Portable graymap", [".pgm"], "image/x-portable-graymap", true),
            Image("ppm", "Portable pixmap", [".ppm"], "image/x-portable-pixmap", true)
        ];
    }
    public async Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var format = Formats.Single(x => x.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (source.Length > RasterImages.MaximumBytes) return new("", "Image exceeds processing limits");
        var header = new byte[1032]; var count = await source.ReadAsync(header, cancellationToken);
        if (!HeaderMatches(format.Mime, header.AsSpan(0, count))) return new("", "Image contents do not match extension");
        var runtime = await ocr.CheckAsync(token: cancellationToken);
        if (!runtime.Available) return new("", "OCR tool unavailable");
        var temporary = Path.Combine(Path.GetTempPath(), "stash-ocr-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(temporary);
        else Directory.CreateDirectory(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var text = new StringBuilder(); var pages = new List<OcrPage>(); var truncated = false; var note = "";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            var info = await RasterImages.DescribeAsync(source, path, budget.Token); truncated = info.Truncated; note = info.Note;
            for (var page = 1; page <= info.Pages; page++)
            {
                try
                {
                    var png = Path.Combine(temporary, "page.png");
                    await File.WriteAllBytesAsync(png, await RasterImages.RenderAsync(source, path, page, ocr: true, token: budget.Token), budget.Token);
                    var rotation = runtime.OrientationAvailable ? await ocr.OrientationAsync(png, budget.Token) : 0;
                    if (rotation != 0) await File.WriteAllBytesAsync(png, await RasterImages.RenderAsync(source, path, page, ocr: true, rotate: rotation, token: budget.Token), budget.Token);
                    var result = await ocr.ReadAsync(png, budget.Token);
                    if (page > 1) text.Append('\f');
                    var take = Math.Min(result.Text.Length, Math.Max(0, DocumentTextReader.MaxCharacters - text.Length));
                    text.Append(result.Text.AsSpan(0, take)); pages.Add(new(page, rotation, take, result.Confidence));
                    truncated |= result.Truncated || take < result.Text.Length;
                    if (text.Length >= DocumentTextReader.MaxCharacters) { truncated |= page < info.Pages; break; }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { if (pages.Count == 0) return new("", "Text extraction timed out"); truncated = true; note = "OCR reached its time budget; saved completed pages. Extract again to retry."; break; }
                catch (TimeoutException)
                { if (pages.Count == 0) return new("", "Text extraction timed out"); truncated = true; note = "OCR timed out on a later page; saved completed pages."; break; }
            }
            var artifact = new ImageOcrArtifact("Tesseract", ocr.Language, pages, truncated, note);
            return new(text.ToString(), string.IsNullOrWhiteSpace(text.ToString()) ? "No text found" : "Indexed", Truncated: truncated, ArtifactJson: JsonSerializer.Serialize(artifact));
        }
        catch (StorageException) { return new("", "Image could not be decoded or exceeds limits"); }
        catch (InvalidDataException) { return new("", "Text extraction failed"); }
        finally { try { Directory.Delete(temporary, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public Task<RasterInfo> DescribeAsync(Stream source, string name, CancellationToken token) => RasterImages.DescribeAsync(source, name, token);
    public Task<byte[]> RenderAsync(Stream source, string name, int page, CancellationToken token) => RasterImages.RenderAsync(source, name, page, token: token);
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => mime switch
    {
        "image/png" => header.StartsWith(new byte[] {137,80,78,71,13,10,26,10}),
        "image/jpeg" => header.StartsWith(new byte[] {255,216,255}),
        "image/gif" => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8),
        "image/webp" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8,4).SequenceEqual("WEBP"u8),
        "image/bmp" => header.StartsWith("BM"u8),
        "image/x-dib" => header.Length >= 40 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) is 40 or 52 or 56 or 108 or 124,
        "image/tiff" => header.StartsWith("II\x2a\0"u8) || header.StartsWith("MM\0\x2a"u8) || header.StartsWith("II\x2b\0"u8) || header.StartsWith("MM\0\x2b"u8),
        "image/x-icon" => header.StartsWith(new byte[] {0,0,1,0}),
        "image/x-portable-bitmap" => header.StartsWith("P1"u8) || header.StartsWith("P4"u8),
        "image/x-portable-graymap" => header.StartsWith("P2"u8) || header.StartsWith("P5"u8),
        "image/x-portable-pixmap" => header.StartsWith("P3"u8) || header.StartsWith("P6"u8),
        "image/avif" => IsoBrand(header, ["avif", "avis"]),
        "image/heic" => IsoBrand(header, ["heic", "heix", "hevc", "hevx", "mif1", "msf1"]),
        _ => false
    };
    private static bool IsoBrand(ReadOnlySpan<byte> header, string[] brands)
    {
        if (header.Length < 16 || !header.Slice(4,4).SequenceEqual("ftyp"u8)) return false;
        var boxLength = Math.Min((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[..4]), header.Length);
        for (var offset = 8; offset + 4 <= boxLength; offset += offset == 8 ? 8 : 4)
            foreach (var brand in brands) if (header.Slice(offset,4).SequenceEqual(Encoding.ASCII.GetBytes(brand))) return true;
        return false;
    }
}
