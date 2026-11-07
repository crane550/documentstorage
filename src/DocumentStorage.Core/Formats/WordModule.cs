using System.Text;
namespace DocumentStorage.Core.Formats;

public sealed class WordModule : IDocumentFormatModule
{
    public IReadOnlyList<DocumentFormat> Formats { get; } = [
        new("word-openxml", "1", "Word document", [".docx"], true, "document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 100L * 1024 * 1024, "Text preview; original page layout is not rendered."),
        new("word-legacy", "1", "Word 97–2003", [".doc"], false, "unsupported", "application/octet-stream", 0, "Convert to DOCX to extract text.")
    ];
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => mime == Formats[0].Mime && header.StartsWith("PK\x03\x04"u8);
    public async Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var package = new OfficePackage(path);
            var zip = package.Zip;
            var parts = zip.Entries.Where(x => x.FullName == "word/document.xml" || System.Text.RegularExpressions.Regex.IsMatch(x.FullName, @"^word/(header|footer)\d*\.xml$"))
                .OrderBy(x => x.FullName == "word/document.xml" ? 0 : 1).ThenBy(x => x.FullName, StringComparer.Ordinal).ToList();
            if (!parts.Any(x => x.FullName == "word/document.xml")) return new("", "Text extraction failed");
            var text = new StringBuilder();
            foreach (var part in parts)
            {
                using var reader = package.Reader(part);
                var more = await reader.ReadAsync();
                while (more && text.Length < DocumentTextReader.MaxCharacters)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.NamespaceURI == WordNamespace && reader.LocalName == "t" && !reader.IsEmptyElement)
                    {
                        // Reading the content leaves the reader on the next node already.
                        text.Append(await reader.ReadElementContentAsStringAsync());
                        more = !reader.EOF;
                        continue;
                    }
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.NamespaceURI == WordNamespace)
                    {
                        if (reader.LocalName == "tab") text.Append('\t');
                        else if (reader.LocalName is "br" or "cr") text.Append('\n');
                    }
                    else if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.NamespaceURI == WordNamespace && reader.LocalName == "p") text.Append('\n');
                    more = await reader.ReadAsync();
                }
                text.Append('\n');
            }
            var result = text.Length > DocumentTextReader.MaxCharacters ? text.ToString(0, DocumentTextReader.MaxCharacters) : text.ToString();
            return string.IsNullOrWhiteSpace(result) ? new("", "No text") : new(result.Replace("\0", ""), "Indexed", Truncated: text.Length > DocumentTextReader.MaxCharacters);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        { return new("", "Text extraction failed"); }
    }
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

}
