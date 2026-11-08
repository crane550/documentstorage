using ImageMagick;

namespace DocumentStorage.Core.Imaging;

public sealed record RasterInfo(int Pages, int TotalFrames, uint Width, uint Height, bool Truncated, string Note);
public interface IRasterPreviewModule
{
    Task<RasterInfo> DescribeAsync(Stream source, string name, CancellationToken token);
    Task<byte[]> RenderAsync(Stream source, string name, int page, CancellationToken token);
}

// One bounded native decode at a time. No shell, delegate, SVG, PDF, URL or indirect input.
public static class RasterImages
{
    public const long MaximumBytes = 100L * 1024 * 1024, MaximumPixels = 40_000_000;
    public const int MaximumPages = 20;
    private static readonly SemaphoreSlim decoder = new(1, 1);
    private static readonly Lazy<bool> initialized = new(() =>
    {
        var configuration = ImageMagick.Configuration.ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*"/>
              <policy domain="filter" rights="none" pattern="*"/>
              <policy domain="path" rights="none" pattern="@*"/>
              <policy domain="coder" rights="none" pattern="*"/>
              <policy domain="coder" rights="read|write" pattern="{PNG,JPEG,BMP,BMP2,BMP3,DIB,GIF,WEBP,AVIF,HEIC,TIFF,ICO,ICON,PNM,PBM,PGM,PPM}"/>
            </policymap>
            """;
        MagickNET.Initialize(configuration);
        ResourceLimits.Memory = 512UL * 1024 * 1024;
        ResourceLimits.Disk = 0; ResourceLimits.Thread = 2; ResourceLimits.Width = 16000; ResourceLimits.Height = 16000;
        ResourceLimits.ListLength = 256; ResourceLimits.MaxProfileSize = 4UL * 1024 * 1024;
        return true;
    });
    public static MagickFormat Format(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => MagickFormat.Png, ".jpg" or ".jpeg" or ".jpe" or ".jfif" => MagickFormat.Jpeg,
        ".bmp" => MagickFormat.Bmp, ".dib" => MagickFormat.Dib, ".gif" => MagickFormat.Gif,
        ".webp" => MagickFormat.WebP, ".avif" => MagickFormat.Avif, ".tif" or ".tiff" => MagickFormat.Tiff,
        ".heic" or ".heif" => MagickFormat.Heic, ".ico" => MagickFormat.Ico,
        ".pbm" => MagickFormat.Pbm, ".pgm" => MagickFormat.Pgm, ".ppm" => MagickFormat.Ppm,
        _ => throw new StorageException("This raster image format is not supported.")
    };
    public static async Task<RasterInfo> DescribeAsync(Stream source, string name, CancellationToken token = default)
        => await DecodeAsync(() => Describe(source, name), token);

    private static RasterInfo Describe(Stream source, string name)
    {
        if (source.Length > MaximumBytes) throw new StorageException("The image exceeds the 100 MB processing limit.");
        source.Position = 0;
        using var images = new MagickImageCollection(); images.Ping(source, new MagickReadSettings { Format = Format(name) });
        if (images.Count == 0) throw new StorageException("The image contains no readable frames.");
        var multipage = Format(name) == MagickFormat.Tiff;
        var pages = multipage ? Math.Min(images.Count, MaximumPages) : 1;
        foreach (var image in images.Take(pages)) ValidateSize(image.Width, image.Height);
        var note = multipage && images.Count > pages ? $"Showing the first {pages} of {images.Count} pages; OCR uses the same page limit."
            : !multipage && images.Count > 1 ? "Preview and OCR use the first frame; download the original for all frames or icon sizes." : "";
        return new(pages, images.Count, images[0].Width, images[0].Height, multipage && images.Count > pages, note);
    }
    public static async Task<byte[]> RenderAsync(Stream source, string name, int page, bool ocr = false, int rotate = 0, CancellationToken token = default)
        => await DecodeAsync(() =>
        {
            var info = Describe(source, name);
            if (page < 1 || page > info.Pages) throw new StorageException("Choose an available image page.");
            source.Position = 0;
            using var image = new MagickImage(source, new MagickReadSettings { Format = Format(name), FrameIndex = (uint)(page - 1), FrameCount = 1 });
            ValidateSize(image.Width, image.Height);
            image.AutoOrient();
            image.BackgroundColor = MagickColors.White; image.Alpha(AlphaAction.Remove); image.ColorSpace = ColorSpace.sRGB;
            if (rotate != 0) image.Rotate(rotate);
            var geometry = new MagickGeometry(ocr ? 4200u : 2400u, ocr ? 4200u : 2400u) { Greater = true };
            image.Resize(geometry);
            if (ocr) { image.ColorSpace = ColorSpace.Gray; image.Deskew(new Percentage(40)); image.Density = new Density(300); }
            image.Strip();
            return image.ToByteArray(MagickFormat.Png);
        }, token);

    private static void ValidateSize(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 16000 || height > 16000 || (long)width * height > MaximumPixels)
            throw new StorageException("The image exceeds the 40 megapixel processing limit. Resize a copy before importing it.");
    }
    private static async Task<T> DecodeAsync<T>(Func<T> action, CancellationToken token)
    {
        await decoder.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); _ = initialized.Value;
                try { var result = action(); token.ThrowIfCancellationRequested(); return result; }
                catch (MagickException) { throw new StorageException("The image could not be decoded. It may be damaged, unsupported by this decoder, or exceed processing limits."); }
            }, token);
        }
        finally { decoder.Release(); }
    }
}
