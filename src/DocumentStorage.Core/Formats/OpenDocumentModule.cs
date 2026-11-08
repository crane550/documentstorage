using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DocumentStorage.Core.Formats;

public sealed class OpenDocumentModule : IDocumentFormatModule
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0", Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0", Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public IReadOnlyList<DocumentFormat> Formats { get; } = [
        new("opendocument-text", "1", "OpenDocument text", [".odt"], true, "document", "application/vnd.oasis.opendocument.text", 100L*1024*1024, "Text preview; original page layout and embedded objects are not rendered."),
        new("opendocument-sheet", "1", "OpenDocument spreadsheet", [".ods"], true, "spreadsheet", "application/vnd.oasis.opendocument.spreadsheet", 100L*1024*1024, "Worksheet preview using saved values. Formulas, links and scripts are never executed.")];
    public bool HeaderMatches(string mime, ReadOnlySpan<byte> header) => Formats.Any(x=>x.Mime==mime) && header.StartsWith("PK\x03\x04"u8);
    public Task<TextResult> ReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(()=>Read(path,cancellationToken),cancellationToken);
    private static TextResult Read(string path, CancellationToken token)
    {
        try
        {
            using var package = new OfficePackage(path);
            var spreadsheet = Path.GetExtension(path).Equals(".ods",StringComparison.OrdinalIgnoreCase);
            var mime = package.Zip.GetEntry("mimetype") ?? throw new InvalidDataException();
            if(mime.Length>128) throw new InvalidDataException();
            using(var reader=new StreamReader(mime.Open()))
                if(reader.ReadToEnd() != "application/vnd.oasis.opendocument."+(spreadsheet?"spreadsheet":"text")) return new("","Office contents do not match extension");
            if(package.Zip.GetEntry("META-INF/manifest.xml")!=null && package.Xml("META-INF/manifest.xml").Descendants().Any(x=>x.Name.LocalName=="encryption-data")) return new("","Password-protected document");
            var content=package.Xml("content.xml");
            var body=content.Root?.Element(Office+"body")?.Element(Office+(spreadsheet?"spreadsheet":"text")) ?? throw new InvalidDataException();
            var text=new StringBuilder(); var truncated=false;
            if(!spreadsheet)
            {
                foreach(var paragraph in body.Descendants().Where(IsParagraph).Where(x=>!x.Ancestors().Any(IsExcluded)))
                {
                    token.ThrowIfCancellationRequested();
                    var value=Paragraph(paragraph,token);
                    if(!Append(text,value+"\n")){truncated=true;break;}
                }
                return new(text.ToString(),text.Length==0?"No text":"Indexed",Truncated:truncated);
            }
            var sections=new List<PreviewSection>(); var previewChars=0;var previewCells=0;var previewTruncated=false;var totalCells=0;
            var sheets=body.Elements(Table+"table").ToList();
            if(sheets.Count>100)truncated=true;
            foreach(var sheet in sheets.Take(100))
            {
                token.ThrowIfCancellationRequested();
                var name=Bound((string?)sheet.Attribute(Table+"name")??"Sheet",160);
                if(!Append(text,$"\n[Sheet: {name}]\n")){truncated=true;break;}
                var rows=new List<PreviewRow>(); var rowNumber=1L;
                foreach(var row in sheet.Descendants(Table+"table-row").Where(x=>x.Ancestors(Table+"table").FirstOrDefault()==sheet))
                {
                    token.ThrowIfCancellationRequested();
                    var repeats=Repeat(row,Table+"number-rows-repeated");
                    var values=new List<(int Column,string Value,int Count)>();long column=1;
                    foreach(var cell in row.Elements().Where(x=>x.Name==Table+"table-cell"||x.Name==Table+"covered-table-cell"))
                    {
                        token.ThrowIfCancellationRequested();
                        var count=Repeat(cell,Table+"number-columns-repeated");
                        var value=CellValue(cell,token);
                        if(value.Length>4096){value=Bound(value,4096);truncated=true;}
                        if(value.Length>0)
                        {
                            if(column>16384){truncated=true;break;}
                            var allowed=(int)Math.Min(count,16385-column);
                            values.Add(((int)column,value,allowed));if(allowed<count)truncated=true;
                        }
                        column=Math.Min(16385,column+count);
                    }
                    if(values.Count>0)
                    {
                        if(rowNumber>1048576){truncated=true;break;}
                        var end=Math.Min(1048577,rowNumber+repeats);
                        if(end<rowNumber+repeats)truncated=true;
                        for(var number=rowNumber;number<end;number++)
                        {
                            token.ThrowIfCancellationRequested();var cells=new List<PreviewCell>();
                            foreach(var (col,value,count) in values)
                            {
                                for(var c=col;c<col+count;c++)
                                {
                                    if(++totalCells>100000){truncated=true;break;}
                                    var reference=ColumnName(c)+number.ToString(CultureInfo.InvariantCulture);
                                    if(!Append(text,reference+": "+value+"\n")){truncated=true;break;}
                                    if(number<=200&&c<=50&&sections.Count<20&&previewCells<3000&&previewChars+value.Length<=150000)
                                    {cells.Add(new(reference,c,value));previewCells++;previewChars+=value.Length;}
                                    else previewTruncated=true;
                                }
                                if(totalCells>100000||text.Length>=DocumentTextReader.MaxCharacters)break;
                            }
                            if(cells.Count>0)rows.Add(new((int)number,cells));
                            if(totalCells>100000||text.Length>=DocumentTextReader.MaxCharacters)break;
                        }
                    }
                    rowNumber=Math.Min(1048577,rowNumber+repeats);
                    if(totalCells>100000||text.Length>=DocumentTextReader.MaxCharacters)break;
                }
                if(sections.Count<20)sections.Add(new(name,"sheet:"+name,rows));else previewTruncated=true;
                if(totalCells>100000||text.Length>=DocumentTextReader.MaxCharacters)break;
            }
            if(sections.Count==0)return new("","No text");
            return new(text.ToString(),"Indexed",Truncated:truncated,ArtifactJson:JsonSerializer.Serialize(new DocumentArtifact(sections,truncated||previewTruncated)));
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or XmlException or ArgumentException or FormatException or OverflowException)
        {return new("","Text extraction failed");}
    }
    private static string CellValue(XElement cell,CancellationToken token)
    {
        var paragraphs=cell.Descendants().Where(IsParagraph).Where(x=>!x.Ancestors().Any(IsExcluded)).Select(x=>Paragraph(x,token));
        var visible=string.Join("\n",paragraphs);if(visible.Length>0)return visible;
        foreach(var attribute in new[]{"string-value","value","date-value","time-value","boolean-value"})
            if(cell.Attribute(Office+attribute) is {} value)return value.Value;
        return cell.Attribute(Table+"formula") is {} formula?"[formula has no saved result: "+Bound(formula.Value,200)+"]":"";
    }
    private static bool IsParagraph(XElement node)=>node.Name==Text+"p"||node.Name==Text+"h";
    private static bool IsExcluded(XElement node)=>node.Name==Text+"tracked-changes"||node.Name==Office+"scripts"||node.Name==Office+"binary-data";
    private static string Paragraph(XElement paragraph,CancellationToken token)
    {
        var text=new StringBuilder();Visit(paragraph,0);return text.ToString();
        void Visit(XNode node,int depth)
        {
            token.ThrowIfCancellationRequested();if(depth>128)throw new InvalidDataException("OpenDocument nesting exceeds limits.");
            if(text.Length>=DocumentTextReader.MaxCharacters)return;
            if(node is XText value)Append(text,value.Value);
            else if(node is XElement element&&!IsExcluded(element))
            {
                if(element.Name==Text+"s")text.Append(' ',(int)Math.Min(Repeat(element,Text+"c"),DocumentTextReader.MaxCharacters-text.Length));
                else if(element.Name==Text+"tab")text.Append('\t');
                else if(element.Name==Text+"line-break")text.Append('\n');
                else foreach(var child in element.Nodes())Visit(child,depth+1);
            }
        }
    }
    private static long Repeat(XElement element,XName name)
    {
        if(element.Attribute(name) is not {} value)return 1;
        if(!long.TryParse(value.Value,NumberStyles.None,CultureInfo.InvariantCulture,out var repeat)||repeat<1||repeat>int.MaxValue)throw new InvalidDataException("Invalid OpenDocument repetition.");
        return repeat;
    }
    internal static string ColumnName(int column){var result="";while(column>0){column--;result=(char)('A'+column%26)+result;column/=26;}return result;}
    private static string Bound(string value,int limit)=>value[..Math.Min(limit,value.Length)];
    private static bool Append(StringBuilder text,string value){var remaining=DocumentTextReader.MaxCharacters-text.Length;text.Append(value.AsSpan(0,Math.Min(remaining,value.Length)));return value.Length<=remaining;}
}
