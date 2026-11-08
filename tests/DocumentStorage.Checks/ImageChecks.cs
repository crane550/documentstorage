using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Core.Formats;
using DocumentStorage.Core.Imaging;
using DocumentStorage.Data;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.EntityFrameworkCore;

internal static class ImageChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool,string> check, Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        var ocr = new TesseractOcr(); var runtime = await ocr.CheckAsync(refresh:true);
        check(runtime.Available && runtime.Languages.Contains("eng"), "the real local Tesseract engine and English data are available");
        var module = new ImageModule(ocr); var registry = DocumentFormatRegistry.CreateDefault(ocr:ocr);
        var reader = new DocumentTextReader(formats:registry);
        check(new[]{".jpg",".jpeg",".jpe",".jfif",".png",".bmp",".dib",".gif",".webp",".avif",".tif",".tiff",".heic",".heif",".ico",".pbm",".pgm",".ppm"}.All(x=>registry.Find("synthetic"+x)?.Definition.ExtractsText==true), "all declared raster formats provide OCR through the shared registry");
        // Actual pixels, not a fake OCR response. Fonts are fixture-only system fonts.
        using var image = new MagickImage(MagickColors.White,1600,1400);
        var draw = new Drawables().Font(OperatingSystem.IsWindows()?"Arial":"DejaVu-Sans").FontPointSize(42).FillColor(MagickColors.Black);
        string[] lines=["SYNTHETIC CEDAR BANK", "MONTHLY ACCOUNT STATEMENT", "Account ending 1234", "Statement date October 31, 2026", "Opening balance 1200.00", "Deposits received 2400.00", "Purchases and payments 1100.00", "Closing balance 2500.00", "Keep this statement for your records", "Synthetic document for local OCR testing"];
        for(var i=0;i<lines.Length;i++) draw.Text(90,120+i*110,lines[i]);
        draw.Draw(image);
        foreach(var (extension,format) in new[]{("png",MagickFormat.Png),("jpg",MagickFormat.Jpeg),("bmp",MagickFormat.Bmp),("gif",MagickFormat.Gif),("webp",MagickFormat.WebP),("avif",MagickFormat.Avif),("dib",MagickFormat.Dib),("pbm",MagickFormat.Pbm),("pgm",MagickFormat.Pgm),("ppm",MagickFormat.Ppm)})
        {
            var path=Path.Combine(scratch,"synthetic."+extension);image.Write(path,format);var before=await File.ReadAllBytesAsync(path);
            var result=await reader.ReadAsync(path);
            check(result.Status=="Indexed"&&result.Text.Contains("CEDAR BANK")&&result.Text.Contains("1234")&&result.Text.Contains("2500.00"), extension+" pixels are decoded and read by real OCR");
            var after=await File.ReadAllBytesAsync(path);check(before.SequenceEqual(after), extension+" OCR preserves the original file bytes");
            var artifact=JsonSerializer.Deserialize<ImageOcrArtifact>(result.ArtifactJson)!;
            check(artifact.Pages.Count==1&&artifact.Pages[0].MeanConfidence is > 65, extension+" retains page-level OCR quality metadata");
        }
        var heic=Path.Combine(scratch,"synthetic.heic");File.Copy(Path.Combine(AppContext.BaseDirectory,"Fixtures","synthetic-statement.heic"),heic);
        check((await reader.ReadAsync(heic)).Text.Contains("CEDAR BANK"),"HEVC-compressed phone HEIC images are decoded and read by the bundled decoder");
        var png=Path.Combine(scratch,"synthetic.png");
        var missing=new DocumentTextReader(formats:DocumentFormatRegistry.CreateDefault(ocr:new TesseractOcr(Path.Combine(scratch,"missing-engine"))));
        check((await missing.ReadAsync(png)).Status=="OCR tool unavailable", "missing OCR tooling is explicit and never invents text");
        var language=new TesseractOcr(language:"synthetic_missing_language");
        check(!(await language.CheckAsync()).Available,"missing configured language data is diagnosed separately from document contents");
        await File.WriteAllTextAsync(Path.Combine(scratch,"fake.png"),"<html>not image content</html>");
        check((await reader.ReadAsync(Path.Combine(scratch,"fake.png"))).Status=="Image contents do not match extension","mislabeled source files never enter the native image decoder");
        using(var blank=new MagickImage(MagickColors.White,800,800))blank.Write(Path.Combine(scratch,"blank.png"));
        check((await reader.ReadAsync(Path.Combine(scratch,"blank.png"))).Status=="No text found","blank images have an honest no-text result and no fabricated reading");
        using(var rotated=(MagickImage)image.Clone()) { rotated.Rotate(90);rotated.Write(Path.Combine(scratch,"rotated.png")); }
        var rotatedResult=await reader.ReadAsync(Path.Combine(scratch,"rotated.png"));
        check(rotatedResult.Text.Contains("CEDAR BANK")&&JsonSerializer.Deserialize<ImageOcrArtifact>(rotatedResult.ArtifactJson)!.Pages[0].Rotation!=0,"a sideways scan is detected, rotated for OCR and stored without rotating the original");
        using(var exif=(MagickImage)image.Clone()) { exif.Rotate(90);exif.Orientation=Orientation.LeftBottom;exif.Write(Path.Combine(scratch,"oriented.jpg")); }
        check((await reader.ReadAsync(Path.Combine(scratch,"oriented.jpg"))).Text.Contains("CEDAR BANK"),"EXIF orientation is respected before OCR");
        using(var pages=new MagickImageCollection())
        {
            pages.Add(image.Clone());
            using var second=new MagickImage(MagickColors.White,1600,1400);
            new Drawables().Font(OperatingSystem.IsWindows()?"Arial":"DejaVu-Sans").FontPointSize(48).FillColor(MagickColors.Black).Text(100,150,"SECOND PAGE CONFIRMATION").Text(100,300,"Synthetic approved total 7350.25").Draw(second);
            pages.Add(second.Clone());pages.Write(Path.Combine(scratch,"multipage.tiff"),MagickFormat.Tiff);
        }
        var tiff=Path.Combine(scratch,"multipage.tiff");var tiffResult=await reader.ReadAsync(tiff);
        check(tiffResult.Text.Contains("CEDAR BANK")&&tiffResult.Text.Contains("7350.25")&&tiffResult.Text.Contains('\f')&&JsonSerializer.Deserialize<ImageOcrArtifact>(tiffResult.ArtifactJson)!.Pages.Count==2,"multipage TIFF OCR saves all bounded pages with real page separators");
        await using(var stream=File.OpenRead(tiff))
        {
            var info=await RasterImages.DescribeAsync(stream,tiff);var rendered=await RasterImages.RenderAsync(stream,tiff,2);
            using var output=new MagickImage(rendered);
            check(info.Pages==2&&output.Width==1600&&output.Format==MagickFormat.Png,"TIFF pages produce normalized PNG previews independently of browser TIFF support");
            await refuses(()=>RasterImages.RenderAsync(stream,tiff,3),"out-of-range TIFF page requests are refused");
        }
        using(var many=new MagickImageCollection())
        { for(var i=0;i<21;i++)many.Add(new MagickImage(MagickColors.White,20,20));many.Write(Path.Combine(scratch,"bounded.tiff"),MagickFormat.Tiff); }
        await using(var stream=File.OpenRead(Path.Combine(scratch,"bounded.tiff")))
        { var info=await RasterImages.DescribeAsync(stream,"bounded.tiff");check(info.Pages==20&&info.Truncated&&info.TotalFrames==21,"multipage bounds are disclosed rather than silently hiding extra pages"); }
        using(var icon=new MagickImage(MagickColors.Blue,64,64))icon.Write(Path.Combine(scratch,"synthetic.ico"),MagickFormat.Ico);
        await using(var stream=File.OpenRead(Path.Combine(scratch,"synthetic.ico")))
        { var rendered=await RasterImages.RenderAsync(stream,"synthetic.ico",1);using var decoded=new MagickImage(rendered);check(decoded.Width==64&&decoded.Format==MagickFormat.Png,"Windows icons use the same bounded converted preview path"); }
        using(var animation=new MagickImageCollection())
        { animation.Add(image.Clone());animation.Add(new MagickImage(MagickColors.White,1600,1400));animation.Write(Path.Combine(scratch,"animated.gif"),MagickFormat.Gif); }
        var animated=await reader.ReadAsync(Path.Combine(scratch,"animated.gif"));var animationInfo=JsonSerializer.Deserialize<ImageOcrArtifact>(animated.ArtifactJson)!;
        check(animated.Text.Contains("CEDAR BANK")&&animationInfo.Pages.Count==1&&animationInfo.Note.Contains("first frame"),"animated images read one frame and disclose that limit");
        // Queue integration uses an isolated root so the worker handles only these fixtures.
        var rootPath=Path.Combine(scratch,"queued");Directory.CreateDirectory(Path.Combine(rootPath,"Inbox"));File.Copy(png,Path.Combine(rootPath,"Inbox","scan.png"));
        var library=new LibraryService(factory,reader,deferExtraction:true);
        while(await library.ProcessNextExtractionAsync()){}
        var root=await library.AddRootAsync("Synthetic image OCR",rootPath,false);var folder=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.Name=="Inbox");
        var entry=(await library.BrowseAsync(folder.Id)).Items.Single();await library.ProcessNextExtractionAsync();entry=(await library.EntryAsync(entry.Id))!;
        check(entry.TextStatus=="Indexed"&&entry.ExtractionSourceHash.Length==64&&(await library.BrowseAsync(null,"CEDAR BANK 1234")).Items.Any(x=>x.Id==entry.Id),"background image OCR persists text and feeds normal search");
        int imageJobs;await using(var db=await factory.CreateDbContextAsync())imageJobs=await db.ExtractionJobs.CountAsync(x=>x.EntryId==entry.Id);
        await library.ScanAsync();while(await library.ProcessNextExtractionAsync()){} // Other completed suites can have their own newly discovered fixture work.
        await using(var db=await factory.CreateDbContextAsync())check(await db.ExtractionJobs.CountAsync(x=>x.EntryId==entry.Id)==imageJobs,"navigation and unchanged scans reuse saved image OCR without starting new work");
        await library.SetIntakeAsync(folder.Id,true);await Task.Delay(5500);await library.ScanAsync();
        await using(var db=await factory.CreateDbContextAsync())check(await db.ExtractionJobs.CountAsync(x=>x.EntryId==entry.Id)==imageJobs&&(await library.EntryAsync(entry.Id))!.TextStatus=="Indexed","making an existing folder an inbox retains image OCR instead of reading unchanged bytes again");
        await using(var db=await factory.CreateDbContextAsync()) { var current=await db.Entries.FindAsync(entry.Id);current!.ExtractionTruncated=true;await db.SaveChangesAsync(); }
        check((await library.SortCardAsync(entry.Id))!.Notes.Any(x=>x.Contains("Only part"))&&!(await library.SortCardAsync(entry.Id))!.Sure,"partial image extraction remains visible and cannot qualify for unattended filing");
        await using(var db=await factory.CreateDbContextAsync()) { var current=await db.Entries.FindAsync(entry.Id);current!.ExtractionTruncated=false;current.ExtractionArtifactJson=JsonSerializer.Serialize(new ImageOcrArtifact("Tesseract","eng",[new(1,0,100,25)],false,""));await db.SaveChangesAsync(); }
        check((await library.SortCardAsync(entry.Id))!.Notes.Any(x=>x.Contains("uncertain text"))&&!(await library.SortCardAsync(entry.Id))!.Sure,"low OCR confidence is surfaced on the filing slip for verification");
        async Task RunDocument(LibraryService worker)
        {
            for(var cycle=0;cycle<1000;cycle++)
            {
                await using var db=await factory.CreateDbContextAsync();
                if(!await db.ExtractionJobs.AnyAsync(x=>x.EntryId==entry.Id&&(x.State=="Queued"||x.State=="Retry")&&x.NotBefore<=DateTimeOffset.UtcNow))return;
                if(!await worker.ProcessNextExtractionAsync())throw new Exception("Image fixture work was not claimed.");
            }
            throw new Exception("Image fixture queue did not settle.");
        }
        var unavailableLibrary=new LibraryService(factory,missing,deferExtraction:true);await unavailableLibrary.ReextractDocumentAsync(entry.Id,"image-owner");await RunDocument(unavailableLibrary);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var waiting=await db.ExtractionJobs.SingleAsync(x=>x.EntryId==entry.Id&&x.State=="Retry");var current=await db.Entries.FindAsync(entry.Id);
            check(waiting.Attempts==0&&current!.TextStatus=="OCR tool unavailable"&&current.ExtractedText.Contains("1234"),"unavailable OCR preserves saved text and waits without consuming the retry budget");
            waiting.NotBefore=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();
        }
        await RunDocument(library);check((await library.EntryAsync(entry.Id))!.TextStatus=="Indexed","waiting OCR resumes after the engine becomes available without re-uploading the image");
    }
}
