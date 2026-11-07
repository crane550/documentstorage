namespace DocumentStorage.Core.Formats;

public sealed class ImageModule : IDocumentFormatModule
{
    private const long Limit = 32L * 1024 * 1024;
    public IReadOnlyList<DocumentFormat> Formats { get; } = [
        new("png", "1", "PNG image", [".png"], false, "image", "image/png", Limit, "Visual preview; OCR is not installed."),
        new("jpeg", "1", "JPEG image", [".jpg", ".jpeg"], false, "image", "image/jpeg", Limit, "Visual preview; OCR is not installed."),
        new("gif", "1", "GIF image", [".gif"], false, "image", "image/gif", Limit),
        new("webp", "1", "WebP image", [".webp"], false, "image", "image/webp", Limit),
        new("bmp", "1", "BMP image", [".bmp"], false, "image", "image/bmp", Limit),
        new("avif", "1", "AVIF image", [".avif"], false, "image", "image/avif", Limit)
    ];
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => mime switch
    {
        "image/png" => header.StartsWith(new byte[] {137,80,78,71,13,10,26,10}),
        "image/jpeg" => header.StartsWith(new byte[] {255,216,255}),
        "image/gif" => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8),
        "image/webp" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8,4).SequenceEqual("WEBP"u8),
        "image/bmp" => header.StartsWith("BM"u8),
        "image/avif" => Avif(header),
        _ => false
    };
    private static bool Avif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header.Slice(4,4).SequenceEqual("ftyp"u8)) return false;
        var boxLength = Math.Min((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[..4]), header.Length);
        for (var offset = 8; offset + 4 <= boxLength; offset += offset == 8 ? 8 : 4)
            if (header.Slice(offset,4).SequenceEqual("avif"u8) || header.Slice(offset,4).SequenceEqual("avis"u8)) return true;
        return false;
    }
}
