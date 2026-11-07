using System.Text;

namespace DocumentStorage.Core.Formats;

public sealed class PlainTextModule : IDocumentFormatModule
{
    public IReadOnlyList<DocumentFormat> Formats { get; } = [new("plain-text", "1", "Text and structured text", [".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini"], true, "text", "text/plain", long.MaxValue)];
    public async Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
        var buffer = new char[DocumentTextReader.MaxCharacters + 1];
        try
        {
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (buffer.AsSpan(0, count).Contains('\0')) return new("", "Text extraction failed");
            return new(new string(buffer, 0, Math.Min(count, DocumentTextReader.MaxCharacters)), "Indexed", Truncated: count > DocumentTextReader.MaxCharacters);
        }
        catch (DecoderFallbackException) { return new("", "Unsupported text encoding"); }
    }
}

public sealed class SourceModule : IDocumentFormatModule
{
    public IReadOnlyList<DocumentFormat> Formats { get; } = [
        new("svg-source", "1", "SVG", [".svg"], false, "text", "text/plain", long.MaxValue, "Escaped source preview. Visual rendering and OCR are not installed."),
        new("html-source", "1", "HTML", [".html", ".htm"], false, "text", "text/plain", long.MaxValue, "Escaped source preview.")
    ];
}
