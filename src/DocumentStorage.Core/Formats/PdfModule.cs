using System.Diagnostics;
using System.Text;
namespace DocumentStorage.Core.Formats;

public sealed class PdfModule(string pdfCommand = "pdftotext") : IDocumentFormatModule
{
    public IReadOnlyList<DocumentFormat> Formats { get; } = [new("pdf", "1", "PDF", [".pdf"], true, "pdf", "application/pdf", 250L * 1024 * 1024, "Reads an existing text layer; pixel OCR is not installed.")];
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => mime == "application/pdf" && header.IndexOf("%PDF-"u8) is >= 0 and < 1024;
    public async Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new ProcessStartInfo(pdfCommand) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-f", "1", "-l", "100", "-enc", "UTF-8", path, "-" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Could not start PDF text extraction.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // Drain both streams; cap retained text so malformed PDFs cannot fill process memory.
            var textTask = ReadBoundedAsync(process.StandardOutput, DocumentTextReader.MaxCharacters + 1, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var text = await textTask;
                await errorTask;
                return process.ExitCode != 0 ? new("", "Text extraction failed") :
                    string.IsNullOrWhiteSpace(text) ? new("", "Needs OCR") : new(text[..Math.Min(text.Length, DocumentTextReader.MaxCharacters)].Replace("\0", ""), "Indexed", Truncated: text.Length > DocumentTextReader.MaxCharacters);
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
