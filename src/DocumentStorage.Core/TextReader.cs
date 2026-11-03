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
