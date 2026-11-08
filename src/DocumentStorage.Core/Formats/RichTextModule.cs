using System.Globalization;
using System.Text;

namespace DocumentStorage.Core.Formats;

// RTF is parsed as a bounded text stream. Destinations carrying fields, objects or pictures
// are skipped; none of the embedded payloads are rendered or executed.
public sealed class RichTextModule : IDocumentFormatModule
{
    public IReadOnlyList<DocumentFormat> Formats { get; }=[new("rich-text","1","Rich Text document",[".rtf"],true,"document","application/rtf",16L*1024*1024,"Text preview; embedded objects, pictures and field instructions are excluded.")];
    public bool HeaderMatches(string mime,ReadOnlySpan<byte> header)=>mime==Formats[0].Mime&&header.StartsWith("{\\rtf"u8);
    public async Task<TextResult> ReadAsync(string path,CancellationToken cancellationToken=default)
    {
        try
        {
            await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            if(file.Length>16L*1024*1024)return new("","Rich Text input exceeds limits");
            var bytes=new byte[(int)file.Length];await file.ReadExactlyAsync(bytes,cancellationToken);
            if(!HeaderMatches(Formats[0].Mime,bytes))return new("","Rich Text contents do not match extension");
            return await Task.Run(()=>Parse(bytes,cancellationToken),cancellationToken);
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {return new("","Text extraction failed");}
    }
    private sealed record State(bool Skip=false,bool Hidden=false,int UnicodeFallback=1,int CodePage=1252);
    private static readonly HashSet<string> Destinations=["fonttbl","colortbl","stylesheet","info","pict","object","objdata","filetbl","datastore","themedata","listtable","listoverridetable","xmlnstbl","generator","fldinst","header","footer","annotation"];
    private static TextResult Parse(byte[] bytes,CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text=new StringBuilder();var pending=new List<byte>();var states=new Stack<State>();var state=new State();var fallback=0;var truncated=false;var closed=false;
        for(var i=0;i<bytes.Length;i++)
        {
            if((i&1023)==0)token.ThrowIfCancellationRequested();var ch=(char)bytes[i];
            if(closed){if(!char.IsWhiteSpace(ch))throw new InvalidDataException();continue;}
            if(ch=='{')
            {
                Flush();if(states.Count>=256)throw new InvalidDataException("Rich Text nesting exceeds limits.");states.Push(state);continue;
            }
            if(ch=='}')
            {
                Flush();if(states.Count==0)throw new InvalidDataException();state=states.Pop();if(states.Count==0)closed=true;continue;
            }
            if(states.Count==0)throw new InvalidDataException();
            if(ch is '\r' or '\n')continue;
            if(ch!='\\'){Byte(bytes[i]);continue;}
            if(++i>=bytes.Length)throw new InvalidDataException();ch=(char)bytes[i];
            if(ch is '\\' or '{' or '}'){Byte((byte)ch);continue;}
            if(ch=='\'')
            {
                if(i+2>=bytes.Length||!byte.TryParse(Encoding.ASCII.GetString(bytes,i+1,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var value))throw new InvalidDataException();
                Byte(value);i+=2;continue;
            }
            Flush();
            if(ch=='*'){state=state with{Skip=true};continue;}
            if(!char.IsAsciiLetter(ch))
            {
                if(fallback>0){fallback--;continue;}
                if(ch=='~')Write("\u00a0");else if(ch=='_')Write("\u2011");else if(ch=='-')Write("\u00ad");continue;
            }
            var start=i;while(i<bytes.Length&&char.IsAsciiLetter((char)bytes[i]))i++;
            var word=Encoding.ASCII.GetString(bytes,start,i-start);int? number=null;
            var sign=1;if(i<bytes.Length&&bytes[i]=='-'){sign=-1;i++;}
            start=i;while(i<bytes.Length&&bytes[i] is >=(byte)'0' and <=(byte)'9')i++;
            if(i>start){if(!int.TryParse(Encoding.ASCII.GetString(bytes,start,i-start),NumberStyles.None,CultureInfo.InvariantCulture,out var parsed))throw new InvalidDataException();number=checked(sign*parsed);}
            if(i<bytes.Length&&bytes[i]==' '){}else i--;
            if(word=="bin")
            {
                if(number is null or <0||number>bytes.Length-i-1)throw new InvalidDataException();i+=number.Value;continue;
            }
            if(Destinations.Contains(word)){state=state with{Skip=true};continue;}
            if(word=="uc"){if(number is null or <0 or >32)throw new InvalidDataException();state=state with{UnicodeFallback=number.Value};continue;}
            if(word=="ansicpg"){if(number is null or <1 or >65535)throw new InvalidDataException();Encoding.GetEncoding(number.Value);state=state with{CodePage=number.Value};continue;}
            if(word=="v"){state=state with{Hidden=number!=0};continue;}
            if(word=="plain"){state=state with{Hidden=false};continue;}
            if(word=="u")
            {
                if(number is null or < -32768 or >65535)throw new InvalidDataException();Write(((char)(ushort)number.Value).ToString());fallback=state.UnicodeFallback;continue;
            }
            if(fallback>0){if(word is "tab" or "line" or "par")fallback--;continue;}
            switch(word)
            {
                case "par":case "line":case "row":case "sect":Write("\n");break;
                case "tab":case "cell":Write("\t");break;
                case "emdash":Write("\u2014");break;case "endash":Write("\u2013");break;
                case "lquote":Write("\u2018");break;case "rquote":Write("\u2019");break;
                case "ldblquote":Write("\u201c");break;case "rdblquote":Write("\u201d");break;
                case "bullet":Write("\u2022");break;
            }
        }
        Flush();if(states.Count!=0||!closed)throw new InvalidDataException();
        // A malformed RTF Unicode escape (or a character cap through a surrogate pair)
        // must not send invalid UTF-16 to the catalog's UTF-8 connection.
        var result=Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text.ToString()));
        return new(result,string.IsNullOrWhiteSpace(result)?"No text":"Indexed",Truncated:truncated);
        void Byte(byte value){if(fallback>0){fallback--;return;}if(!state.Skip&&!state.Hidden){pending.Add(value);}}
        void Flush(){if(pending.Count==0)return;Write(Encoding.GetEncoding(state.CodePage).GetString(pending.ToArray()));pending.Clear();}
        void Write(string value){if(state.Skip||state.Hidden)return;var remaining=DocumentTextReader.MaxCharacters-text.Length;var clean=value.Replace("\0","");text.Append(clean.AsSpan(0,Math.Min(remaining,clean.Length)));if(clean.Length>remaining)truncated=true;}
    }
}
