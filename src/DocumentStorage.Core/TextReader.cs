using System.Diagnostics;
using System.Text;

namespace DocumentStorage.Core;

public sealed record TextResult(string Text, string Status);

public sealed class DocumentTextReader(string pdfCommand = "pdftotext")
{
    public const int MaxCharacters = 500_000;
    public async Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (new[] { ".txt", ".md", ".csv" }.Contains(extension))
        {
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            var buffer = new char[MaxCharacters];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            return new(new string(buffer, 0, count).Replace("\0", ""), "Indexed");
        }
        if (extension == ".docx") return await ReadDocxAsync(path, cancellationToken);
        if (extension != ".pdf") return new("", "Filename only");
        try
        {
            var info = new ProcessStartInfo(pdfCommand) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-f", "1", "-l", "100", "-enc", "UTF-8", path, "-" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Could not start PDF text extraction.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // Drain both streams; cap retained text so malformed PDFs cannot fill process memory.
            var textTask = ReadBoundedAsync(process.StandardOutput, MaxCharacters, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var text = await textTask;
                await errorTask;
                return process.ExitCode != 0 ? new("", "Text extraction failed") :
                    string.IsNullOrWhiteSpace(text) ? new("", "Needs OCR") : new(text.Replace("\0", ""), "Indexed");
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                await ((Task)Task.WhenAll(textTask, errorTask)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                cancellationToken.ThrowIfCancellationRequested();
                return new("", "Text extraction timed out");
            }
        }
        catch (System.ComponentModel.Win32Exception) { return new("", "PDF text tool unavailable"); }
    }

    // A .docx is a zip of XML: the text is in w:t runs, paragraphs in w:p, tabs and breaks
    // in w:tab and w:br. Read the main document plus headers and footers, bounded.
    private static async Task<TextResult> ReadDocxAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read);
            var parts = zip.Entries.Where(x => x.FullName == "word/document.xml" || System.Text.RegularExpressions.Regex.IsMatch(x.FullName, @"^word/(header|footer)\d*\.xml$"))
                .OrderBy(x => x.FullName == "word/document.xml" ? 0 : 1).ThenBy(x => x.FullName, StringComparer.Ordinal).ToList();
            if (!parts.Any(x => x.FullName == "word/document.xml")) return new("", "Text extraction failed");
            var text = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Length > 50_000_000) continue;
                await using var stream = part.Open();
                using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, Async = true });
                var more = await reader.ReadAsync();
                while (more && text.Length < MaxCharacters)
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
            var result = text.Length > MaxCharacters ? text.ToString(0, MaxCharacters) : text.ToString();
            return string.IsNullOrWhiteSpace(result) ? new("", "No text") : new(result.Replace("\0", ""), "Indexed");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        { return new("", "Text extraction failed"); }
    }
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            if (result.Length < maximum) result.Append(buffer, 0, Math.Min(count, maximum - result.Length));
        return result.ToString();
    }
}
