using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentStorage.Core.Imaging;

public sealed record OcrRuntimeStatus(bool Available, string Version, string Language, string[] Languages, bool OrientationAvailable, string Message);
public sealed record OcrPage(int Page, int Rotation, int Characters, double? MeanConfidence);
public sealed record ImageOcrArtifact(string Engine, string Language, List<OcrPage> Pages, bool Truncated, string Note);
public sealed record OcrOutput(string Text, double? Confidence, bool Truncated);

public sealed partial class TesseractOcr(string? command = null, string language = "eng")
{
    public string Language { get; } = Regex.IsMatch(language, "^[a-zA-Z0-9_]+(?:\\+[a-zA-Z0-9_]+)*$", RegexOptions.CultureInvariant) && language.Length <= 100
        ? language : throw new ArgumentException("Use installed OCR language codes, for example eng or eng+spa.");
    private readonly string executable = command ?? DefaultCommand();
    private readonly SemaphoreSlim probeGate = new(1, 1);
    private OcrRuntimeStatus? cached; private DateTimeOffset checkedAt;
    public static string DefaultCommand()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe");
        return OperatingSystem.IsWindows() && File.Exists(installed) ? installed : "tesseract";
    }
    public async Task<OcrRuntimeStatus> CheckAsync(bool refresh = false, CancellationToken token = default)
    {
        await probeGate.WaitAsync(token);
        try
        {
            if (!refresh && cached != null && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromSeconds(30)) return cached;
            checkedAt = DateTimeOffset.UtcNow;
            try
            {
                var version = await RunAsync(["--version"], 4096, TimeSpan.FromSeconds(5), token);
                var languages = await RunAsync(["--list-langs"], 32000, TimeSpan.FromSeconds(5), token);
                var installed = languages.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(x => Regex.IsMatch(x, "^[a-zA-Z0-9_]+$", RegexOptions.CultureInvariant)).ToArray();
                var available = version.Code == 0 && languages.Code == 0 && Language.Split('+').All(x => installed.Contains(x));
                return cached = new(available, version.Output.Split('\n')[0].Trim()[..Math.Min(version.Output.Split('\n')[0].Trim().Length, 100)], Language, installed,
                    installed.Contains("osd"), available ? "Ready for local image OCR." : "OCR language data is unavailable. Install the configured languages and check again.");
            }
            catch (System.ComponentModel.Win32Exception) { return cached = new(false, "", Language, [], false, "Tesseract is unavailable to the service. Install Tesseract OCR, then check again."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            { return cached = new(false, "", Language, [], false, "The service could not run Tesseract. Check its installation and permissions."); }
        }
        finally { probeGate.Release(); }
    }
    public async Task<int> OrientationAsync(string png, CancellationToken token)
    {
        try
        {
            var result = await RunAsync([png, "stdout", "-l", "osd", "--psm", "0"], 4096, TimeSpan.FromSeconds(8), token);
            var match = Rotation().Match(result.Output);
            return result.Code == 0 && match.Success && int.TryParse(match.Groups[1].Value, out var rotate) && rotate is 90 or 180 or 270 ? rotate : 0;
        }
        catch (TimeoutException) { return 0; } // Short receipts may not contain enough text for orientation detection.
    }
    public async Task<OcrOutput> ReadAsync(string png, CancellationToken token)
    {
        var result = await RunAsync([png, "stdout", "-l", Language, "--psm", "3", "tsv"], 3_000_000, TimeSpan.FromSeconds(45), token);
        if (result.Code != 0) throw new InvalidDataException("OCR could not read the normalized image.");
        var text = new StringBuilder(); var confidences = new List<double>(); string previous = "";
        foreach (var row in result.Output.Split('\n').Skip(1))
        {
            var cells = row.TrimEnd('\r').Split('\t', 12);
            if (cells.Length != 12 || cells[0] != "5" || string.IsNullOrWhiteSpace(cells[11])) continue;
            var line = string.Join(':', cells.Skip(1).Take(4));
            if (text.Length > 0) text.Append(line == previous ? ' ' : '\n');
            text.Append(cells[11].Replace("\0", "")); previous = line;
            if (double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) && confidence >= 0) confidences.Add(confidence);
            if (text.Length > DocumentTextReader.MaxCharacters) break;
        }
        return new(text.ToString(0, Math.Min(text.Length, DocumentTextReader.MaxCharacters)), confidences.Count > 0 ? Math.Round(confidences.Average(), 1) : null,
            result.Truncated || text.Length > DocumentTextReader.MaxCharacters);
    }
    private async Task<(int Code, string Output, bool Truncated)> RunAsync(string[] args, int maximum, TimeSpan timeout, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        start.Environment["OMP_THREAD_LIMIT"] = "2";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("OCR process could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        var output = DrainAsync(process.StandardOutput, maximum, deadline.Token); var errors = DrainAsync(process.StandardError, 4096, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token); var result = await output; await errors;
            return (process.ExitCode, result.Text, result.Truncated);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await ((Task)Task.WhenAll(output, errors)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            token.ThrowIfCancellationRequested(); throw new TimeoutException("OCR timed out.");
        }
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[8192]; var truncated = false; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        { var take = Math.Min(count, maximum - text.Length); text.Append(buffer, 0, take); truncated |= take < count; }
        return (text.ToString(), truncated);
    }
    [GeneratedRegex(@"(?m)^Rotate:\s*(\d+)\s*$", RegexOptions.CultureInvariant)] private static partial Regex Rotation();
}
