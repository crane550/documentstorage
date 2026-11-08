namespace DocumentStorage.Core.Formats;

public sealed record DocumentFormat(string Id, string Version, string Name, string[] Extensions,
    bool ExtractsText, string PreviewKind, string Mime, long PreviewLimit, string Notes = "");

public interface IDocumentFormatModule
{
    IReadOnlyList<DocumentFormat> Formats { get; }
    Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(new TextResult("", "Filename only"));
    bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => false;
}

public sealed record RegisteredFormat(DocumentFormat Definition, IDocumentFormatModule Module);

// Add a module here (or supply a registry at composition time), without changing the scanner,
// search, filing or AI reader. Preview kinds are shared presentation contracts, not extensions.
public sealed class DocumentFormatRegistry
{
    private readonly Dictionary<string, RegisteredFormat> extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDocumentFormatModule> modules;
    public static DocumentFormatRegistry Default { get; } = CreateDefault();
    public static DocumentFormatRegistry CreateDefault(string pdfCommand = "pdftotext", Imaging.TesseractOcr? ocr = null, Pdf.PopplerPdfTools? pdfTools = null) => new([
        new PlainTextModule(), new PdfModule(pdfCommand, ocr, pdfTools), new WordModule(), new SpreadsheetModule(), new ImageModule(ocr), new SourceModule()
    ]);

    public DocumentFormatRegistry(IEnumerable<IDocumentFormatModule> modules)
    {
        this.modules = modules.ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in this.modules)
            foreach (var format in module.Formats)
            {
                if (format.Id.Length == 0 || format.Version.Length == 0 || format.Extensions.Length == 0 || !ids.Add(format.Id)) throw new ArgumentException("A format needs a unique ID, version and extensions.");
                foreach (var extension in format.Extensions)
                    if (!extension.StartsWith('.') || !extensions.TryAdd(extension, new(format, module))) throw new ArgumentException("Format extensions must be unique and start with a dot.");
            }
    }
    public IReadOnlyList<DocumentFormat> Formats => modules.SelectMany(x => x.Formats).ToList();
    public RegisteredFormat? Find(string name) => extensions.GetValueOrDefault(Path.GetExtension(name));
    public PreviewFormat Preview(string name) => Find(name)?.Definition is { } format ? new(format.PreviewKind, format.Mime, format.PreviewLimit) : new("unsupported", "application/octet-stream", 0);
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header)
    {
        foreach (var module in modules) if (module.HeaderMatches(mime, header)) return true;
        return false;
    }
    public string VersionFor(string name) => Find(name)?.Definition is { } format ? format.Id + ":" + format.Version : "unknown:1";
}

public sealed record DocumentArtifact(List<PreviewSection> Sections, bool Truncated);
public sealed record PreviewSection(string Name, string Locator, List<PreviewRow> Rows);
public sealed record PreviewRow(int Number, List<PreviewCell> Cells);
public sealed record PreviewCell(string Reference, int Column, string Text);
public sealed record DerivedPreview(string Text, bool Truncated, DocumentArtifact? Artifact);
