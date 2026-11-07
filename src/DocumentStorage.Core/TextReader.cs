using DocumentStorage.Core.Formats;
namespace DocumentStorage.Core;

public sealed record TextResult(string Text, string Status, string FormatId = "", string FormatVersion = "", bool Truncated = false, string ArtifactJson = "");

// Compatibility facade: format-specific code lives in registered modules.
public sealed class DocumentTextReader(string pdfCommand = "pdftotext", DocumentFormatRegistry? formats = null)
{
    public const int MaxCharacters = 500_000;
    public DocumentFormatRegistry Formats { get; } = formats ?? DocumentFormatRegistry.CreateDefault(pdfCommand);
    public async Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var registered = Formats.Find(path);
        if (registered == null) return new("", "Filename only", "unknown", "1");
        var format = registered.Definition;
        if (!format.ExtractsText) return new("", "Filename only", format.Id, format.Version);
        var result = await registered.Module.ReadAsync(path, cancellationToken);
        return result with { FormatId = format.Id, FormatVersion = format.Version };
    }
}
