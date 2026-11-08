using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DocumentStorage.Core.Formats;

public sealed class PresentationModule : IDocumentFormatModule
{
    private static readonly XNamespace P="http://schemas.openxmlformats.org/presentationml/2006/main", A="http://schemas.openxmlformats.org/drawingml/2006/main", R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public IReadOnlyList<DocumentFormat> Formats { get; }=[new("powerpoint-openxml","1","PowerPoint presentation",[".pptx"],true,"presentation","application/vnd.openxmlformats-officedocument.presentationml.presentation",100L*1024*1024,"Slide text and speaker notes. Original slide layout, pictures, audio and video are not rendered.")];
    public bool HeaderMatches(string mime,ReadOnlySpan<byte> header)=>mime==Formats[0].Mime&&header.StartsWith("PK\x03\x04"u8);
    public Task<TextResult> ReadAsync(string path,CancellationToken cancellationToken=default)=>Task.Run(()=>Read(path,cancellationToken),cancellationToken);
    private static TextResult Read(string path,CancellationToken token)
    {
        try
        {
            using var package=new OfficePackage(path);
            var presentation=package.Xml("ppt/presentation.xml");var relations=Relations(package,"ppt/presentation.xml");
            var ids=presentation.Root?.Element(P+"sldIdLst")?.Elements(P+"sldId").ToList()??throw new InvalidDataException();
            var text=new StringBuilder();var sections=new List<PreviewSection>();var truncated=ids.Count>200;var previewTruncated=false;var previewCharacters=0;var number=0;
            foreach(var id in ids.Take(200))
            {
                token.ThrowIfCancellationRequested();number++;
                if(!relations.TryGetValue((string?)id.Attribute(R+"id")??"",out var relation)||!relation.Type.EndsWith("/slide",StringComparison.Ordinal))throw new InvalidDataException();
                var part=OfficePackage.Resolve("ppt/presentation.xml",relation.Target);var slide=package.Xml(part);
                if(slide.Root?.Name!=P+"sld")throw new InvalidDataException();
                var body=Paragraphs(slide.Descendants(A+"p"),token);var notes="";
                foreach(var noteRelation in Relations(package,part).Values.Where(x=>x.Type.EndsWith("/notesSlide",StringComparison.Ordinal)))
                {
                    var note=package.Xml(OfficePackage.Resolve(part,noteRelation.Target));
                    var shapes=note.Descendants(P+"sp").Where(x=>!x.Descendants(P+"ph").Any(ph=>(string?)ph.Attribute("type") is "sldNum" or "dt" or "ftr" or "hdr" or "sldImg"));
                    notes=Paragraphs(shapes.SelectMany(x=>x.Descendants(A+"p")),token);break;
                }
                var title=slide.Descendants(P+"sp").FirstOrDefault(x=>x.Descendants(P+"ph").Any(ph=>(string?)ph.Attribute("type") is "title" or "ctrTitle"));
                var name=title==null?"Slide "+number:Paragraphs(title.Descendants(A+"p"),token).Trim();
                if(name.Length==0)name="Slide "+number;name=name[..Math.Min(name.Length,160)];
                if(!Append(text,$"\n[Slide {number}: {name}]\n"+body+(notes.Length>0?"\n[Speaker notes]\n"+notes:"")))truncated=true;
                if(sections.Count<50 && previewCharacters<150000)
                {
                    var previewBody=body[..Math.Min(body.Length,150000-previewCharacters)];previewCharacters+=previewBody.Length;
                    var previewNotes=notes[..Math.Min(notes.Length,150000-previewCharacters)];previewCharacters+=previewNotes.Length;
                    previewTruncated|=previewBody.Length<body.Length||previewNotes.Length<notes.Length;
                    sections.Add(new(name,"slide:"+number,[new(1,[new("body",1,previewBody)]),new(2,[new("notes",1,previewNotes)])]));
                }
                else previewTruncated=true;
                if(text.Length>=DocumentTextReader.MaxCharacters)break;
            }
            return new(text.ToString(),ids.Count==0?"No text":"Indexed",Truncated:truncated,ArtifactJson:JsonSerializer.Serialize(new DocumentArtifact(sections,truncated||previewTruncated)));
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or XmlException or ArgumentException or FormatException)
        {return new("","Text extraction failed");}
    }
    private static Dictionary<string,(string Target,string Type)> Relations(OfficePackage package,string part)
    {
        var index=part.LastIndexOf('/');var path=part[..(index+1)]+"_rels/"+part[(index+1)..]+".rels";
        if(package.Zip.GetEntry(path)==null)return [];
        return package.Xml(path).Root?.Elements().Where(x=>x.Name.LocalName=="Relationship"&&(string?)x.Attribute("TargetMode")!="External")
            .ToDictionary(x=>(string?)x.Attribute("Id")??"",x=>((string?)x.Attribute("Target")??"",(string?)x.Attribute("Type")??""))??[];
    }
    private static string Paragraphs(IEnumerable<XElement> paragraphs,CancellationToken token)
    {
        var text=new StringBuilder();foreach(var p in paragraphs)
        {
            token.ThrowIfCancellationRequested();
            foreach(var node in p.Descendants().Where(x=>x.Name==A+"t"||x.Name==A+"br"))
                if(!Append(text,node.Name==A+"br"?"\n":node.Value))return text.ToString();
            if(!Append(text,"\n"))break;
        }
        return text.ToString();
    }
    private static bool Append(StringBuilder text,string value){var remaining=DocumentTextReader.MaxCharacters-text.Length;text.Append(value.AsSpan(0,Math.Min(value.Length,remaining)));return value.Length<=remaining;}
}
