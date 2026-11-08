using DocumentStorage.Core.Office;
namespace DocumentStorage.Core.Formats;
public sealed class LegacyOfficeModule(OfficeConverter? converter=null):IDocumentFormatModule
{
    private readonly OfficeConverter reader=converter??new();
    public IReadOnlyList<DocumentFormat> Formats{get;}=[
        new("word-legacy","2","Word 97–2003",[".doc"],true,"document","application/msword",50L*1024*1024,"Text preview through local LibreOffice conversion; original layout and objects are omitted."),
        new("excel-legacy","2","Excel 97–2003",[".xls"],true,"spreadsheet","application/vnd.ms-excel",50L*1024*1024,"Worksheet preview through local LibreOffice conversion; values reflect the converted workbook.")];
    public Task<TextResult> ReadAsync(string path,CancellationToken token=default)=>reader.ReadLegacyAsync(path,token);
    public bool HeaderMatches(string mime,ReadOnlySpan<byte> header)=>Formats.Any(x=>x.Mime==mime)&&header.StartsWith(new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1});
}
