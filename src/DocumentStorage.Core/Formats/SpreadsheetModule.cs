using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DocumentStorage.Core.Formats;

public sealed class SpreadsheetModule : IDocumentFormatModule
{
    public const int PreviewRows = 200, PreviewColumns = 50, MaximumSheets = 100;
    public IReadOnlyList<DocumentFormat> Formats { get; } = [
        new("excel-openxml", "1", "Excel workbook", [".xlsx"], true, "spreadsheet", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 100L * 1024 * 1024, "Sheet/table preview. Uses saved formula results; does not recalculate formulas or render charts."),
        new("excel-legacy", "1", "Excel 97–2003", [".xls"], false, "unsupported", "application/octet-stream", 0, "Convert to XLSX to extract cells.")
    ];
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => mime == Formats[0].Mime && header.StartsWith("PK\x03\x04"u8);
    public Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() => Read(path, cancellationToken), cancellationToken);

    private static TextResult Read(string path, CancellationToken token)
    {
        try
        {
            using var package = new OfficePackage(path);
            var workbook = package.Xml("xl/workbook.xml");
            var relationships = package.Xml("xl/_rels/workbook.xml.rels").Descendants().Where(x => x.Name.LocalName == "Relationship")
                .Where(x => (string?)x.Attribute("TargetMode") != "External")
                .ToDictionary(x => (string?)x.Attribute("Id") ?? "", x => (Target: (string?)x.Attribute("Target") ?? "", Type: (string?)x.Attribute("Type") ?? ""));
            var date1904 = workbook.Descendants().Any(x => x.Name.LocalName == "workbookPr" && (string?)x.Attribute("date1904") is "1" or "true");
            var strings = SharedStrings(package, token);
            var dates = DateStyles(package);
            var text = new StringBuilder(); var sections = new List<PreviewSection>();
            var previewChars = 0; var previewCells = 0; var truncated = false; var previewTruncated = false;
            var sheets = workbook.Descendants().Where(x => x.Name.LocalName == "sheet").ToList();
            if (sheets.Count > MaximumSheets) truncated = true;
            foreach (var sheet in sheets.Take(MaximumSheets))
            {
                token.ThrowIfCancellationRequested();
                var name = Bound((string?)sheet.Attribute("name") ?? "Sheet", 160);
                var id = sheet.Attributes().FirstOrDefault(x => x.Name.LocalName == "id")?.Value ?? "";
                if (!relationships.TryGetValue(id, out var relation) || !relation.Type.EndsWith("/worksheet", StringComparison.Ordinal)) continue;
                var part = package.Zip.GetEntry(OfficePackage.Resolve("xl/workbook.xml", relation.Target));
                if (part == null) throw new InvalidDataException("Worksheet is missing.");
                var rows = new SortedDictionary<int, List<PreviewCell>>();
                var sectionTruncated = false;
                if (!Append(text, $"\n[Sheet: {name}{((string?)sheet.Attribute("state") is "hidden" or "veryHidden" ? "; hidden" : "")}]\n")) { truncated = true; break; }
                using var reader = package.Reader(part);
                var more = reader.Read(); var cells = 0;
                while (more)
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c") { more = reader.Read(); continue; }
                    if (++cells > 100000) { truncated = true; break; }
                    var cell = (XElement)XNode.ReadFrom(reader); more = !reader.EOF;
                    var reference = (string?)cell.Attribute("r") ?? "";
                    if (!CellLocation(reference, out var column, out var number)) { truncated = true; continue; }
                    var type = (string?)cell.Attribute("t") ?? "";
                    var raw = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value ?? "";
                    var formula = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "f")?.Value;
                    string value;
                    if (type == "s")
                    {
                        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var stringId) || stringId < 0 || stringId >= strings.Count) throw new InvalidDataException("Invalid shared string reference.");
                        value = strings[stringId];
                    }
                    else if (type == "inlineStr") value = string.Concat(cell.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
                    else if (type == "b") value = raw == "1" ? "true" : "false";
                    else value = raw;
                    if (type is "" or "n" && int.TryParse((string?)cell.Attribute("s"), out var style) && dates.TryGetValue(style, out var timeOnly)) value = DateValue(raw, date1904, timeOnly);
                    if (formula != null && raw.Length == 0) value = "[formula has no saved result: " + Bound(formula, 200) + "]";
                    if (value.Length == 0) continue;
                    if (value.Length > 4096) { value = Bound(value, 4096); truncated = true; }
                    if (!Append(text, reference + ": " + value + "\n")) { truncated = true; break; }
                    if (number <= PreviewRows && column <= PreviewColumns && previewCells < 3000 && previewChars + value.Length <= 150000 && sections.Count < 20)
                    {
                        if (!rows.TryGetValue(number, out var row)) rows[number] = row = [];
                        row.Add(new(reference, column, value)); previewCells++; previewChars += value.Length;
                    }
                    else sectionTruncated = true;
                }
                if (sections.Count < 20) sections.Add(new(name, "sheet:" + name, rows.Select(x => new PreviewRow(x.Key, x.Value.OrderBy(c => c.Column).ToList())).ToList()));
                else sectionTruncated = true;
                previewTruncated |= sectionTruncated;
                if (text.Length >= DocumentTextReader.MaxCharacters || cells > 100000) break;
            }
            if (sections.Count == 0) return new("", "Text extraction failed");
            return new(text.ToString(), "Indexed", Truncated: truncated, ArtifactJson: JsonSerializer.Serialize(new DocumentArtifact(sections, truncated || previewTruncated)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or ArgumentException or FormatException or OverflowException)
        { return new("", "Text extraction failed"); }
    }
    private static List<string> SharedStrings(OfficePackage package, CancellationToken token)
    {
        var result = new List<string>(); var total = 0;
        if (package.Zip.GetEntry("xl/sharedStrings.xml") is not { } part) return result;
        using var reader = package.Reader(part); var more = reader.Read();
        while (more)
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                var item = (XElement)XNode.ReadFrom(reader); more = !reader.EOF;
                var value = string.Concat(item.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
                total += value.Length;
                if (result.Count >= 100000 || total > 4_000_000 || value.Length > 100000) throw new InvalidDataException("Shared strings exceed the limit.");
                result.Add(value);
            }
            else more = reader.Read();
        }
        return result;
    }
    private static Dictionary<int, bool> DateStyles(OfficePackage package)
    {
        var result = new Dictionary<int, bool>();
        if (package.Zip.GetEntry("xl/styles.xml") == null) return result;
        var styles = package.Xml("xl/styles.xml");
        var formats = styles.Descendants().Where(x => x.Name.LocalName == "numFmt").ToDictionary(x => (int?)x.Attribute("numFmtId") ?? -1, x => (string?)x.Attribute("formatCode") ?? "");
        var index = 0;
        foreach (var xf in styles.Descendants().FirstOrDefault(x => x.Name.LocalName == "cellXfs")?.Elements() ?? [])
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            if (id is >= 14 and <= 22 or >= 45 and <= 47) result[index] = id is >= 18 and <= 21 or >= 45 and <= 47;
            else if (formats.TryGetValue(id, out var code))
            {
                var stripped = new StringBuilder(); var quoted = false;
                for (var i = 0; i < code.Length; i++)
                {
                    var ch = code[i];
                    if (ch == '"') quoted = !quoted;
                    else if (!quoted && ch is '\\' or '_' or '*') i++;
                    else if (!quoted && ch == '[')
                    {
                        var end = code.IndexOf(']', i + 1);
                        if (end < 0) break;
                        var bracket = code[(i + 1)..end].ToLowerInvariant();
                        if (bracket.Length > 0 && bracket.All(c => c is 'h' or 'm' or 's')) stripped.Append(bracket);
                        i = end; // Colors, conditions, currency and locale tags are not date tokens.
                    }
                    else if (!quoted) stripped.Append(char.ToLowerInvariant(ch));
                }
                var value = stripped.ToString();
                if (value.Contains('y') || value.Contains('d')) result[index] = false;
                else if (value.Contains('h') || value.Contains('s')) result[index] = true;
                else if (value.Contains('m')) result[index] = false;
            }
            index++;
        }
        return result;
    }
    private static string DateValue(string raw, bool date1904, bool timeOnly)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) || !double.IsFinite(serial) || serial < 0 || serial > 2950000) return raw;
        var date = (date1904 ? new DateTime(1904, 1, 1) : serial < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30)).AddDays(serial);
        return date.ToString(timeOnly ? "HH:mm:ss" : date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
    private static bool CellLocation(string reference, out int column, out int row)
    {
        column = 0; row = 0; var i = 0;
        while (i < reference.Length && reference[i] is >= 'A' and <= 'Z') { if (column > 16384) return false; column = column * 26 + reference[i++] - 'A' + 1; }
        return column is > 0 and <= 16384 && int.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out row) && row is > 0 and <= 1048576;
    }
    private static bool Append(StringBuilder text, string value)
    {
        var remaining = DocumentTextReader.MaxCharacters - text.Length;
        text.Append(value.AsSpan(0, Math.Min(value.Length, remaining))); return value.Length <= remaining;
    }
    private static string Bound(string value, int limit) => value[..Math.Min(limit, value.Length)];
}
