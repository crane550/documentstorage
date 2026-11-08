using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentStorage.Core.Imaging;
using DocumentStorage.Core.Pdf;

namespace DocumentStorage.Core.Formats;

public sealed class PdfModule : IDocumentFormatModule
{
    public const int MaximumPages = 100, MaximumOcrPages = 20;
    private readonly PopplerPdfTools tools;
    private readonly TesseractOcr engine;
    private readonly ImageModule images;
    public IReadOnlyList<DocumentFormat> Formats { get; }
    public PdfModule(string pdfCommand = "pdftotext", TesseractOcr? ocr = null, PopplerPdfTools? pdfTools = null)
    {
        tools = pdfTools ?? new(pdfCommand); engine = ocr ?? new(); images = new(engine);
        var version = "2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(engine.Language)))[..8];
        Formats = [new("pdf", version, "PDF", [".pdf"], true, "pdf", "application/pdf", 250L * 1024 * 1024,
            "Uses embedded text first; scanned or text-poor pages get local OCR. Handles mixed PDFs without changing the original.")];
    }
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => mime == "application/pdf" && header.IndexOf("%PDF-"u8) is >= 0 and < 1024;
    public async Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > Formats[0].PreviewLimit) return new("", "PDF exceeds processing limits");
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var header = new byte[1032]; var count = await source.ReadAsync(header, cancellationToken);
            if (!HeaderMatches("application/pdf", header.AsSpan(0, count))) return new("", "PDF contents do not match extension");
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(75));
        string? temporary = null;
        try
        {
            var extracted = await tools.TextAsync(path, MaximumPages, budget.Token); PopplerPdfTools.RequireSuccess(extracted);
            var raw = extracted.Output.Replace("\0", ""); var truncated = extracted.Truncated || raw.Length > DocumentTextReader.MaxCharacters;
            raw = raw[..Math.Min(raw.Length, DocumentTextReader.MaxCharacters)];
            var pages = raw.Split('\f').ToList();
            if (pages.Count > 1 && pages[^1].Length == 0) pages.RemoveAt(pages.Count - 1);
            var totalPages = pages.Count; var note = "";
            // All useful text pages take the fast path; page rendering and Tesseract are unnecessary.
            if ((!truncated && pages.Any(x => !UsefulText(x))) || pages.Count >= MaximumPages)
            {
                var info = await tools.InfoAsync(path, budget.Token); totalPages = info.Pages;
                if (totalPages > MaximumPages) { truncated = true; note = $"Extraction covers the first {MaximumPages} of {totalPages} PDF pages."; }
                while (pages.Count < Math.Min(totalPages, MaximumPages) && !extracted.Truncated) pages.Add("");
            }
            if (pages.Count > MaximumPages) pages = pages.Take(MaximumPages).ToList();
            var details = pages.Select((text, index) => new OcrPage(index + 1, 0, text.Length, null, "embedded", "complete")).ToList();
            if (extracted.Truncated) { note = "PDF text reached its character limit; check the original for omitted content."; return Result(pages, details, totalPages, true, note); }
            var candidates = pages.Select((text, index) => index).Where(index => !UsefulText(pages[index])).ToList();
            if (candidates.Count == 0) return Result(pages, details, totalPages, truncated, note);
            var runtime = await engine.CheckAsync(token: budget.Token);
            if (!runtime.Available) return new("", "OCR tool unavailable");
            temporary = Path.Combine(Path.GetTempPath(), "stash-pdf-ocr-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(temporary);
            else Directory.CreateDirectory(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var attempts = 0;
            foreach (var index in candidates)
            {
                if (attempts++ >= MaximumOcrPages)
                { details[index] = details[index] with { Status = "ocr_page_limit" }; truncated = true; note = $"OCR is limited to {MaximumOcrPages} scanned/text-poor pages per PDF; other embedded text is retained."; continue; }
                try
                {
                    var png = await tools.RenderAsync(path, index + 1, temporary, budget.Token);
                    var read = await images.ReadAsync(png, budget.Token);
                    if (read.Status == "OCR tool unavailable") return read;
                    if (read.Status is not ("Indexed" or "No text found"))
                    { truncated = true; details[index] = details[index] with { Status = "ocr_failed" }; note = "Some PDF pages could not be read with OCR. Existing text is retained; check the original and extract again to retry."; continue; }
                    var artifact = JsonSerializer.Deserialize<ImageOcrArtifact>(read.ArtifactJson);
                    var page = artifact?.Pages.FirstOrDefault();
                    // A rendered page contains both native text and pixels. Choose one reading, avoiding duplicate paragraphs.
                    if (LettersAndDigits(read.Text) > LettersAndDigits(pages[index]) || string.IsNullOrWhiteSpace(pages[index]))
                    { pages[index] = read.Text; details[index] = new(index + 1, page?.Rotation ?? 0, read.Text.Length, page?.MeanConfidence, "ocr", "complete"); }
                    else details[index] = details[index] with { Status = "complete" };
                    truncated |= read.Truncated;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    truncated = true; note = "PDF OCR reached its time budget. Completed pages and embedded text are saved; check the original.";
                    foreach (var remaining in candidates.Where(x => x >= index)) details[remaining] = details[remaining] with { Status = "time_budget" };
                    break;
                }
                catch (TimeoutException)
                { truncated = true; details[index] = details[index] with { Status = "ocr_timed_out" }; note = "OCR timed out on a PDF page. Completed pages and embedded text are retained."; }
                catch (InvalidDataException)
                { truncated = true; details[index] = details[index] with { Status = "ocr_failed" }; note = "A PDF page could not be rendered. Completed pages and embedded text are retained."; }
            }
            return Result(pages, details, totalPages, truncated, note);
        }
        catch (PdfToolUnavailableException ex) { return new("", ex.Message); }
        catch (PdfPasswordException) { return new("", "PDF password required"); }
        catch (TimeoutException) { return new("", "Text extraction timed out"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new("", "Text extraction timed out"); }
        catch (InvalidDataException) { return new("", "Text extraction failed"); }
        finally
        {
            if (temporary != null) try { Directory.Delete(temporary, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    // Small footer/page-number layers are not sufficient evidence that a scanned page is readable.
    private static bool UsefulText(string text) => LettersAndDigits(text) >= 32;
    private static int LettersAndDigits(string text) => text.Count(char.IsLetterOrDigit);
    private TextResult Result(List<string> pages, List<OcrPage> details, int total, bool truncated, string note)
    {
        var output = new StringBuilder();
        for (var index = 0; index < pages.Count; index++)
        {
            if (index > 0 && output.Length < DocumentTextReader.MaxCharacters) output.Append('\f');
            var remaining = DocumentTextReader.MaxCharacters - output.Length;
            var take = Math.Min(pages[index].Length, remaining); output.Append(pages[index].AsSpan(0, take));
            truncated |= take < pages[index].Length;
            details[index] = details[index] with { Characters = take };
        }
        // Poppler ends its extraction with a page separator. Preserve that convention so a
        // module upgrade does not invalidate saved AI readings of unchanged native PDF text.
        if (output.Length < DocumentTextReader.MaxCharacters) output.Append('\f');
        var text = output.ToString();
        return new(text, !string.IsNullOrWhiteSpace(text) ? "Indexed" : truncated ? "PDF OCR incomplete" : "No text found", Truncated: truncated,
            ArtifactJson: JsonSerializer.Serialize(new ImageOcrArtifact("Poppler + Tesseract", engine.Language, details, truncated, note, total)));
    }
}
