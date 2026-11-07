using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace DocumentStorage.Core.Formats;

// Office packages are data. No relationships are fetched, formulas executed or embedded objects opened.
internal sealed class OfficePackage : IDisposable
{
    private readonly FileStream file;
    public ZipArchive Zip { get; }
    public OfficePackage(string path)
    {
        file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (file.Length > 100L * 1024 * 1024) throw new InvalidDataException("Office package exceeds the input limit.");
            Zip = new(file, ZipArchiveMode.Read, leaveOpen: true);
            if (Zip.Entries.Count > 10000 || Zip.Entries.Sum(x => x.Length) > 200L * 1024 * 1024 || Zip.Entries.Select(x => x.FullName).Distinct(StringComparer.Ordinal).Count() != Zip.Entries.Count)
                throw new InvalidDataException("Office package exceeds the unpacked limit.");
        }
        catch { Zip?.Dispose(); file.Dispose(); throw; }
    }
    public XmlReader Reader(ZipArchiveEntry part)
    {
        if (part.Length > 20L * 1024 * 1024) throw new InvalidDataException("Office XML part exceeds the limit.");
        return XmlReader.Create(part.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true, MaxCharactersInDocument = 20L * 1024 * 1024, CloseInput = true });
    }
    public XDocument Xml(string name)
    {
        var part = Zip.GetEntry(name) ?? throw new InvalidDataException("Required Office part is missing.");
        using var reader = Reader(part); return XDocument.Load(reader);
    }
    public static string Resolve(string part, string target)
    {
        if (target.Contains('\\') || target.Contains(':') || target.Contains('?') || target.Contains('#')) throw new InvalidDataException("Invalid package relationship.");
        var segments = target.StartsWith('/') ? new List<string>() : part.Split('/')[..^1].ToList();
        foreach (var segment in target.TrimStart('/').Split('/'))
        {
            var value = Uri.UnescapeDataString(segment);
            if (value == ".") continue;
            if (value == "..") { if (segments.Count == 0) throw new InvalidDataException("Package relationship escapes its root."); segments.RemoveAt(segments.Count - 1); }
            else { if (value.Length == 0 || value.IndexOfAny(['/', '\\', '\0', ':']) >= 0) throw new InvalidDataException("Invalid package relationship."); segments.Add(value); }
        }
        return string.Join('/', segments);
    }
    public void Dispose() { Zip.Dispose(); file.Dispose(); }
}
