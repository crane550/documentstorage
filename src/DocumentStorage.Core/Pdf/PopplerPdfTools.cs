using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentStorage.Core.Pdf;

public sealed record PdfToolStatus(bool Available, bool TextAvailable, bool InfoAvailable, bool RasterAvailable, string Message);
public sealed record PdfToolResult(int Code, string Output, string Error, bool Truncated);
public sealed record PdfInfo(int Pages);
public sealed class PdfToolUnavailableException(string status) : Exception(status);
public sealed class PdfPasswordException : Exception;

// Fixed Poppler operations only. Configuration is trusted instance configuration, never tool arguments.
public sealed class PopplerPdfTools(string textCommand = "pdftotext", string? infoCommand = null, string? rasterCommand = null)
{
    private readonly string infoTool = string.IsNullOrWhiteSpace(infoCommand) ? Sibling(textCommand, "pdfinfo") : infoCommand;
    private readonly string rasterTool = string.IsNullOrWhiteSpace(rasterCommand) ? Sibling(textCommand, "pdftoppm") : rasterCommand;
    public static string Sibling(string configuredText, string name)
    {
        var directory = Path.GetDirectoryName(configuredText);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }
    public async Task<PdfToolStatus> CheckAsync(CancellationToken token = default)
    {
        async Task<bool> Check(string command)
        {
            try { return (await RunAsync(command, ["-v"], 4096, TimeSpan.FromSeconds(5), token)).Code == 0; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or TimeoutException) { return false; }
        }
        var text = await Check(textCommand); var info = await Check(infoTool); var raster = await Check(rasterTool);
        return new(text && info && raster, text, info, raster, text && info && raster
            ? "PDF text extraction and scanned-page rendering are ready."
            : "PDF tools are unavailable to the service. Check the Poppler installation and service permissions.");
    }
    public async Task<PdfToolResult> TextAsync(string path, int pages, CancellationToken token)
    {
        try { return await RunAsync(textCommand, ["-f", "1", "-l", pages.ToString(CultureInfo.InvariantCulture), "-enc", "UTF-8", path, "-"], DocumentTextReader.MaxCharacters + 1, TimeSpan.FromSeconds(30), token); }
        catch (System.ComponentModel.Win32Exception) { throw new PdfToolUnavailableException("PDF text tool unavailable"); }
    }
    public async Task<PdfInfo> InfoAsync(string path, CancellationToken token)
    {
        PdfToolResult result;
        try { result = await RunAsync(infoTool, ["-enc", "UTF-8", path], 65536, TimeSpan.FromSeconds(10), token); }
        catch (System.ComponentModel.Win32Exception) { throw new PdfToolUnavailableException("PDF OCR tools unavailable"); }
        RequireSuccess(result);
        // Metadata values can contain newlines resembling field labels. The tool's real page
        // count follows those values; never accept a spoofed label in a truncated metadata block.
        var match = Regex.Matches(result.Output, @"(?m)^Pages:\s*(\d+)\s*$", RegexOptions.CultureInvariant).LastOrDefault();
        if (result.Truncated || match == null || !int.TryParse(match.Groups[1].Value, out var pages) || pages < 1) throw new InvalidDataException("PDF page count is unavailable.");
        return new(pages);
    }
    public async Task<string> RenderAsync(string path, int page, string temporaryDirectory, CancellationToken token)
    {
        var prefix = Path.Combine(temporaryDirectory, $"rendered-{page}");
        // A failed renderer must never leave a previous page's pixels available as this result.
        if (File.Exists(prefix + ".png")) File.Delete(prefix + ".png");
        PdfToolResult result;
        try
        {
            result = await RunAsync(rasterTool, ["-f", page.ToString(CultureInfo.InvariantCulture), "-l", page.ToString(CultureInfo.InvariantCulture), "-singlefile", "-scale-to", "4200", "-r", "200", "-gray", "-png", path, prefix], 4096, TimeSpan.FromSeconds(20), token);
        }
        catch (System.ComponentModel.Win32Exception) { throw new PdfToolUnavailableException("PDF OCR tools unavailable"); }
        RequireSuccess(result);
        var png = prefix + ".png";
        if (!File.Exists(png) || new FileInfo(png).Length > Imaging.RasterImages.MaximumBytes) throw new InvalidDataException("The PDF page could not be rendered within its limits.");
        return png;
    }
    public static void RequireSuccess(PdfToolResult result)
    {
        if (result.Code == 0) return;
        if (result.Error.Contains("password", StringComparison.OrdinalIgnoreCase)) throw new PdfPasswordException();
        throw new InvalidDataException("The PDF tool could not read this document.");
    }
    private static async Task<PdfToolResult> RunAsync(string command, string[] args, int maximum, TimeSpan duration, CancellationToken token)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        start.Environment["OMP_THREAD_LIMIT"] = "2";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("PDF tool could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(duration);
        var output = DrainAsync(process.StandardOutput, maximum, deadline.Token); var error = DrainAsync(process.StandardError, 4096, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token); var text = await output; var diagnostic = await error;
            return new(process.ExitCode, text.Text, diagnostic.Text, text.Truncated);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await ((Task)Task.WhenAll(output, error)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            token.ThrowIfCancellationRequested(); throw new TimeoutException("PDF processing timed out.");
        }
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[8192]; var truncated = false; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        { var take = Math.Min(count, maximum - text.Length); text.Append(buffer, 0, take); truncated |= take < count; }
        return (text.ToString(), truncated);
    }
}
