using System.Text;
using System.Text.Json;
using DocumentStorage.Core;
using DocumentStorage.Core.Formats;
using DocumentStorage.Core.Imaging;
using DocumentStorage.Core.Pdf;
using DocumentStorage.Data;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.EntityFrameworkCore;

internal static class PdfOcrChecks
{
    public static async Task RunAsync(IDbContextFactory<CatalogDb> factory, string scratch, Action<bool,string> check, Func<Func<Task>,string,Task> refuses)
    {
        Directory.CreateDirectory(scratch);
        var engine=new TesseractOcr();var tools=new PopplerPdfTools();check((await tools.CheckAsync()).Available,"the real PDF text, information and page-rendering tools are available");
        using var scan=new MagickImage(MagickColors.White,1600,2000);
        var draw=new Drawables().Font(OperatingSystem.IsWindows()?"Arial":"DejaVu-Sans").FontPointSize(42).FillColor(MagickColors.Black);
        string[] lines=["SYNTHETIC CEDAR BANK","MONTHLY ACCOUNT STATEMENT","Account ending 1234","Statement date October 31, 2026","Opening balance 1200.00","Deposits received 2400.00","Purchases and payments 1100.00","Closing balance 2500.00","Keep this statement for your records","Synthetic PDF OCR confirmation NORTH7350"];
        for(var i=0;i<lines.Length;i++)draw.Text(90,120+i*115,lines[i]);draw.Draw(scan);
        var jpeg=scan.ToByteArray(MagickFormat.Jpeg);
        var native=BuildPdf([(null,"Synthetic native document with searchable embedded text and NativeTextSentinel 52790.")]);
        var nativePath=Path.Combine(scratch,"native.pdf");await File.WriteAllBytesAsync(nativePath,native);
        var missingEngine=new TesseractOcr(Path.Combine(scratch,"missing-tesseract"));
        var fast=new PdfModule(ocr:missingEngine,pdfTools:new PopplerPdfTools(infoCommand:Path.Combine(scratch,"missing-info"),rasterCommand:Path.Combine(scratch,"missing-renderer")));
        var result=await fast.ReadAsync(nativePath);var artifact=JsonSerializer.Deserialize<ImageOcrArtifact>(result.ArtifactJson)!;
        check(result.Status=="Indexed"&&result.Text.Contains("NativeTextSentinel")&&artifact.Pages.All(x=>x.Source=="embedded"),"useful native PDF text indexes without Tesseract or PDF raster tools");
        check(result.Text==(await tools.TextAsync(nativePath,100,default)).Output,"native PDF cache text stays byte-for-byte compatible with earlier Poppler extraction");
        var unbrokenPath=Path.Combine(scratch,"unbroken.pdf");await File.WriteAllBytesAsync(unbrokenPath,BuildPdf([(null,"NativeSentinel123456789012345678901234567890")]));
        check((await fast.ReadAsync(unbrokenPath)).Status=="Indexed","useful PDF text does not require whitespace-delimited words to avoid OCR");
        var metadataPath=Path.Combine(scratch,"metadata.pdf");await File.WriteAllBytesAsync(metadataPath,BuildPdf([(null,"Synthetic native document with searchable existing text.")],"Synthetic title\nPages: 99999999\n"));
        check((await tools.InfoAsync(metadataPath,default)).Pages==1,"PDF metadata cannot spoof the trusted tool page count with a field-like title");
        var path=Path.Combine(scratch,"scanned.pdf");await File.WriteAllBytesAsync(path,BuildPdf([(jpeg,"")]));var module=new PdfModule(ocr:engine);
        var bytes=await File.ReadAllBytesAsync(path);result=await module.ReadAsync(path);artifact=JsonSerializer.Deserialize<ImageOcrArtifact>(result.ArtifactJson)!;
        check(result.Status=="Indexed"&&result.Text.Contains("CEDAR BANK")&&result.Text.Contains("1234")&&result.Text.Contains("2500.00"),"real OCR reads printed words from an image-only PDF");
        var unchanged=await File.ReadAllBytesAsync(path);check(bytes.SequenceEqual(unchanged),"PDF OCR preserves every original byte instead of rewriting a searchable PDF");
        check(artifact.TotalPages==1&&artifact.Pages.Single().Source=="ocr"&&artifact.Pages.Single().MeanConfidence is >65,"PDF OCR stores page source and confidence provenance");
        var mixedPath=Path.Combine(scratch,"mixed.pdf");await File.WriteAllBytesAsync(mixedPath,BuildPdf([(null,"Synthetic cover page with useful existing text and CoverSentinel 82640."),(jpeg,"")]));
        result=await module.ReadAsync(mixedPath);artifact=JsonSerializer.Deserialize<ImageOcrArtifact>(result.ArtifactJson)!;
        check(result.Text.Contains("CoverSentinel")&&result.Text.Contains("2500.00")&&artifact.Pages[0].Source=="embedded"&&artifact.Pages[1].Source=="ocr","a text cover page does not hide scanned pages later in the same PDF");
        check(result.Text.TrimEnd('\f').Split('\f').Length==2&&LibraryService.SplitPassages(new(){Name="mixed.pdf",ExtractedText=result.Text}).Any(x=>x.Page==2&&x.Text.Contains("2500.00")),"mixed PDF extraction preserves page separators and page-two search evidence");
        var renderDirectory=Path.Combine(scratch,"page-output");Directory.CreateDirectory(renderDirectory);var renderedOne=await tools.RenderAsync(mixedPath,1,renderDirectory,default);var renderedTwo=await tools.RenderAsync(mixedPath,2,renderDirectory,default);
        check(renderedOne!=renderedTwo&&File.Exists(renderedOne)&&File.Exists(renderedTwo),"PDF page renders have distinct outputs and cannot reuse another page's stale pixels");
        var footerPath=Path.Combine(scratch,"footer.pdf");await File.WriteAllBytesAsync(footerPath,BuildPdf([(jpeg,"Page 1")]));
        result=await module.ReadAsync(footerPath);check(result.Text.Contains("2500.00"),"a small page-number text layer does not prevent OCR of the scanned page body");
        using(var sideways=(MagickImage)scan.Clone())
        {
            sideways.Rotate(90);var rotated=Path.Combine(scratch,"rotated.pdf");await File.WriteAllBytesAsync(rotated,BuildPdf([(sideways.ToByteArray(MagickFormat.Jpeg),"")]));result=await module.ReadAsync(rotated);
            check(result.Text.Contains("CEDAR BANK")&&JsonSerializer.Deserialize<ImageOcrArtifact>(result.ArtifactJson)!.Pages.Single().Rotation!=0,"sideways scanned PDF pages reuse scan-orientation handling");
        }
        var blankPath=Path.Combine(scratch,"blank-between.pdf");await File.WriteAllBytesAsync(blankPath,BuildPdf([(null,"Synthetic native first page with enough existing text to avoid OCR."),(null,""),(jpeg,"")]));
        result=await module.ReadAsync(blankPath);check(result.Text.TrimEnd('\f').Split('\f').Length==3&&LibraryService.SplitPassages(new(){Name="blank-between.pdf",ExtractedText=result.Text}).Any(x=>x.Page==3&&x.Text.Contains("2500.00")),"blank intervening PDF pages do not shift later source citations");
        var longPath=Path.Combine(scratch,"long.pdf");await File.WriteAllBytesAsync(longPath,BuildPdf(Enumerable.Range(1,101).Select(i=>((byte[]?)null,$"Synthetic native PDF page {i} has enough useful searchable words.")).ToArray()));
        result=await module.ReadAsync(longPath);artifact=JsonSerializer.Deserialize<ImageOcrArtifact>(result.ArtifactJson)!;
        check(result.Truncated&&artifact.TotalPages==101&&artifact.Pages.Count==100,"PDF text page limits are detected and marked as partial rather than silently complete");
        var limitedPath=Path.Combine(scratch,"limited.pdf");await File.WriteAllBytesAsync(limitedPath,BuildPdf(Enumerable.Range(0,21).Select(i=>((byte[]?)null,"")).ToArray()));
        result=await module.ReadAsync(limitedPath);artifact=JsonSerializer.Deserialize<ImageOcrArtifact>(result.ArtifactJson)!;
        check(result.Truncated&&artifact.Pages.Count==21&&artifact.Pages[20].Status is "ocr_page_limit" or "time_budget","scanned-page or time limits retain explicit per-page incomplete status");
        check(result.Status=="PDF OCR incomplete","an unread PDF with no recovered text is not mislabeled as a completed no-text result");
        check((await new PdfModule(ocr:missingEngine).ReadAsync(path)).Status=="OCR tool unavailable","scanned PDFs report a missing OCR engine explicitly");
        check((await new PdfModule(pdfTools:new PopplerPdfTools(rasterCommand:Path.Combine(scratch,"missing-renderer"))).ReadAsync(path)).Status=="PDF OCR tools unavailable","missing page-rendering tools are distinguished from missing PDF text extraction");
        check((await new PdfModule(pdfCommand:Path.Combine(scratch,"missing-text")).ReadAsync(path)).Status=="PDF text tool unavailable","missing PDF text tools keep their established diagnostic");
        var broken=Path.Combine(scratch,"broken.pdf");await File.WriteAllTextAsync(broken,"%PDF-1.7\nSynthetic malformed content");check((await module.ReadAsync(broken)).Status=="Text extraction failed","malformed PDFs fail without fabricated OCR content");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();var caught=false;try{await module.ReadAsync(path,cancelled.Token);}catch(OperationCanceledException){caught=true;}
            check(caught,"PDF OCR honors cancellation before starting subprocess work");
        }
        var reader=new DocumentTextReader(formats:DocumentFormatRegistry.CreateDefault(ocr:engine));var rootPath=Path.Combine(scratch,"queued");Directory.CreateDirectory(Path.Combine(rootPath,"Inbox"));File.Copy(path,Path.Combine(rootPath,"Inbox","scan.pdf"));
        var library=new LibraryService(factory,reader,deferExtraction:true);while(await library.ProcessNextExtractionAsync()){}
        var root=await library.AddRootAsync("Synthetic scanned PDF OCR",rootPath,false);var folder=(await library.OverviewAsync()).Folders.Single(x=>x.RootId==root&&x.Name=="Inbox");var entry=(await library.BrowseAsync(folder.Id)).Items.Single();
        async Task RunDocument(LibraryService worker)
        {
            for(var cycle=0;cycle<1000;cycle++)
            {
                await using var db=await factory.CreateDbContextAsync();
                if(!await db.ExtractionJobs.AnyAsync(x=>x.EntryId==entry.Id&&(x.State=="Queued"||x.State=="Retry")&&x.NotBefore<=DateTimeOffset.UtcNow))return;
                if(!await worker.ProcessNextExtractionAsync())throw new Exception("PDF fixture work could not be claimed.");
            }
            throw new Exception("PDF fixture queue did not settle.");
        }
        await RunDocument(library);entry=(await library.EntryAsync(entry.Id))!;
        check(entry.TextStatus=="Indexed"&&entry.ExtractionSourceHash.Length==64&&(await library.BrowseAsync(null,"2500.00")).Items.Any(x=>x.Id==entry.Id),"queued scanned PDF OCR persists searchable source-bound results");
        int count;await using(var db=await factory.CreateDbContextAsync())count=await db.ExtractionJobs.CountAsync(x=>x.EntryId==entry.Id);
        await library.ScanAsync();await using(var db=await factory.CreateDbContextAsync())check(count==await db.ExtractionJobs.CountAsync(x=>x.EntryId==entry.Id),"unchanged scans reuse saved PDF OCR rather than repeatedly reading pages");
        var unavailable=new LibraryService(factory,new DocumentTextReader(formats:DocumentFormatRegistry.CreateDefault(ocr:missingEngine)),deferExtraction:true);
        await unavailable.ReextractDocumentAsync(entry.Id,"pdf-owner");await RunDocument(unavailable);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var job=await db.ExtractionJobs.SingleAsync(x=>x.EntryId==entry.Id&&x.State=="Retry");var current=await db.Entries.FindAsync(entry.Id);
            check(job.Attempts==0&&current!.ExtractedText.Contains("2500.00"),"missing OCR preserves the saved PDF index and waits without consuming retry attempts");
            job.NotBefore=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();
        }
        await RunDocument(library);check((await library.EntryAsync(entry.Id))!.TextStatus=="Indexed","queued PDF OCR resumes when its local engine becomes available");
        await library.SetIntakeAsync(folder.Id,true);await Task.Delay(5500);await library.ScanAsync();
        await using(var db=await factory.CreateDbContextAsync()){var current=await db.Entries.FindAsync(entry.Id);current!.ExtractionArtifactJson=JsonSerializer.Serialize(new ImageOcrArtifact("Poppler + Tesseract","eng",[new(1,0,100,25)],false,"",1));await db.SaveChangesAsync();}
        var card=await library.SortCardAsync(entry.Id);check(card!=null&&!card.Sure&&card.Notes.Any(x=>x.Contains("uncertain text")),"low-confidence PDF OCR requires verification just like image OCR");
    }
    internal static byte[] BuildPdf((byte[]? Image,string Text)[] pages, string? title = null)
    {
        var objects=new List<byte[]>();byte[] Bytes(string value)=>Encoding.ASCII.GetBytes(value);
        objects.Add(Bytes("<< /Type /Catalog /Pages 2 0 R >>"));objects.Add([]);objects.Add(Bytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));var ids=new List<int>();
        foreach(var page in pages)
        {
            int imageId=0;
            if(page.Image!=null)
            {
                imageId=objects.Count+1;using var image=new MagickImage(page.Image);using var obj=new MemoryStream();obj.Write(Bytes($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Image.Length} >>\nstream\n"));obj.Write(page.Image);obj.Write(Bytes("\nendstream"));objects.Add(obj.ToArray());
            }
            var text=page.Text.Replace("\\","\\\\").Replace("(","\\(").Replace(")","\\)");var content=(imageId>0?"q 612 0 0 792 0 0 cm /Scan Do Q\n":"")+$"BT /F1 14 Tf 40 25 Td ({text}) Tj ET";
            var contentId=objects.Count+1;objects.Add(Bytes($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"));
            ids.Add(objects.Count+1);objects.Add(Bytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> {(imageId>0?$"/XObject << /Scan {imageId} 0 R >>":"")} >> /Contents {contentId} 0 R >>"));
        }
        objects[1]=Bytes($"<< /Type /Pages /Count {ids.Count} /Kids [{string.Join(' ',ids.Select(x=>$"{x} 0 R"))}] >>");
        var infoId=0;if(title!=null){infoId=objects.Count+1;objects.Add(Bytes($"<< /Title ({title.Replace("\\","\\\\").Replace("(","\\(").Replace(")","\\)")}) >>"));}
        using var output=new MemoryStream();output.Write(Bytes("%PDF-1.4\n"));var offsets=new List<long>{0};
        for(var i=0;i<objects.Count;i++){offsets.Add(output.Position);output.Write(Bytes($"{i+1} 0 obj\n"));output.Write(objects[i]);output.Write(Bytes("\nendobj\n"));}
        var xref=output.Position;output.Write(Bytes($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n"));foreach(var offset in offsets.Skip(1))output.Write(Bytes($"{offset:D10} 00000 n \n"));output.Write(Bytes($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R {(infoId>0?$"/Info {infoId} 0 R":"")} >>\nstartxref\n{xref}\n%%EOF\n"));return output.ToArray();
    }
}
