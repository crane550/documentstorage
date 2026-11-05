using System.Globalization;
using System.Text.RegularExpressions;

namespace DocumentStorage.Core;

public sealed record NamedTerm(Guid Id, string Name, string[] Aliases);
public sealed record KindTerm(Guid Id, string Name, string Description, string[] Fields, bool IsCatchAll);
public sealed record FoundDate(DateOnly Date, string Text, string Where);
public sealed record DocumentHintSet(
    bool DescriptiveName, string[] NameWords, List<FoundDate> Dates, List<Guid> Senders, List<Guid> Tags, List<(Guid Id, int Score)> Kinds);

// Everything Stash can tell about a document without the model: the filename's words and
// dates, and known senders, tags and kinds named in the filename or the first page.
public static partial class DocumentHints
{
    public const int TextWindow = 4000;
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "scan", "scanned", "scansnap", "img", "image", "doc", "document", "file", "page", "pages", "untitled", "copy", "pdf",
        "new", "final", "print", "printed", "attachment", "download", "downloaded", "export", "output", "fax", "dsc", "photo",
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec"
    };
    private static readonly string[] Months = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    // A name like "20261105_143022.pdf", "Scan 12.pdf" or "IMG_0042.pdf" says nothing; a name like
    // "Acme_Invoice_INV-20391.pdf" or "Truck title.pdf" does, and is kept unless the person renames it.
    public static bool IsDescriptive(string fileName) => Words(Path.GetFileNameWithoutExtension(fileName))
        .Where(x => !IsHexish(x)).Select(x => new string(x.Where(char.IsLetter).ToArray())).Any(x => x.Length >= 3 && !Generic.Contains(x));

    private static bool IsHexish(string word) => word.Length >= 8 && word.All(Uri.IsHexDigit) && word.Any(char.IsDigit);

    public static string[] Words(string text) => WordPattern().Matches(text.Replace('_', ' ')).Select(x => x.Value).ToArray();

    public static DocumentHintSet Read(string fileName, string text, IReadOnlyList<KindTerm> kinds, IReadOnlyList<NamedTerm> senders, IReadOnlyList<NamedTerm> tags)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var head = text.Length > TextWindow ? text[..TextWindow] : text;
        var dates = FindDates(stem, "filename").Concat(FindDates(head, "text")).ToList();
        var nameSpace = " " + Normalize(stem) + " ";
        var textSpace = " " + Normalize(head) + " ";
        int Position(string term)
        {
            if (term.Length < 2) return int.MaxValue;
            if (nameSpace.Contains(" " + term + " ", StringComparison.Ordinal)) return 0;
            var at = textSpace.IndexOf(" " + term + " ", StringComparison.Ordinal);
            return at < 0 ? int.MaxValue : 1 + at;
        }
        List<Guid> Matching(IReadOnlyList<NamedTerm> terms) => terms
            .Select(term => (term.Id, Position: new[] { term.Name }.Concat(term.Aliases).Select(x => Position(Normalize(x))).Min()))
            .Where(x => x.Position < int.MaxValue).OrderBy(x => x.Position).Select(x => x.Id).ToList();
        var words = Words(stem + " " + head).Select(x => x.ToLowerInvariant()).Where(x => x.Length >= 3).ToHashSet();
        var nameWords = Words(stem).Select(x => x.ToLowerInvariant()).ToHashSet();
        var scored = kinds.Where(x => !x.IsCatchAll).Select(kind =>
        {
            var kindWords = Words(kind.Name).Select(x => x.ToLowerInvariant()).Where(x => x.Length >= 3).ToArray();
            var named = kindWords.Length > 0 && kindWords.All(x => nameWords.Contains(x) || nameWords.Contains(x + "s") || (x.EndsWith('s') && nameWords.Contains(x[..^1])));
            var phrase = textSpace.Contains(" " + Normalize(kind.Name) + " ", StringComparison.Ordinal);
            var described = Words(kind.Description + " " + string.Join(' ', kind.Fields).Replace('_', ' ')).Select(x => x.ToLowerInvariant()).Where(x => x.Length >= 4).Distinct().Count(words.Contains);
            return (kind.Id, Score: (named ? 10 : 0) + (phrase ? 5 : 0) + described);
        }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        return new(IsDescriptive(fileName), Words(stem).Where(x => !Generic.Contains(x)).ToArray(), dates, Matching(senders), Matching(tags), scored);
    }

    public static string Normalize(string text) => string.Join(' ', Words(text.ToLowerInvariant().Replace("&", " and ")));

    public static List<FoundDate> FindDates(string text, string where)
    {
        var found = new List<FoundDate>();
        void Add(int year, int month, int day, string source)
        {
            if (year is < 1950 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return;
            var date = new DateOnly(year, month, day);
            if (!found.Any(x => x.Date == date)) found.Add(new(date, source, where));
        }
        foreach (Match m in Iso().Matches(text)) Add(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Value);
        foreach (Match m in Compact().Matches(text)) Add(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Value);
        foreach (Match m in American().Matches(text))
        {
            var year = int.Parse(m.Groups[3].Value); if (year < 100) year += 2000;
            Add(year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Value);
        }
        foreach (Match m in Written().Matches(text))
            if (MonthOf(m.Groups[1].Value) is int month) Add(int.Parse(m.Groups[3].Value), month, int.Parse(m.Groups[2].Value), m.Value);
        foreach (Match m in WrittenDayFirst().Matches(text))
            if (MonthOf(m.Groups[2].Value) is int month) Add(int.Parse(m.Groups[3].Value), month, int.Parse(m.Groups[1].Value), m.Value);
        return found;
    }

    private static int? MonthOf(string word)
    {
        word = word.ToLowerInvariant().TrimEnd('.');
        if (word.Length < 3) return null;
        for (var i = 0; i < Months.Length; i++) if (Months[i].StartsWith(word, StringComparison.Ordinal)) return i + 1;
        return null;
    }

    // Is this date written anywhere in the filename or text, in any of the forms above?
    public static bool Mentions(string fileName, string text, DateOnly date) =>
        FindDates(Path.GetFileNameWithoutExtension(fileName), "filename").Concat(FindDates(text, "text")).Any(x => x.Date == date);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)] private static partial Regex WordPattern();
    [GeneratedRegex(@"(?<!\d)(\d{4})[-./_](\d{1,2})[-./_](\d{1,2})(?!\d)", RegexOptions.CultureInvariant)] private static partial Regex Iso();
    [GeneratedRegex(@"(?<!\d)((?:19|20)\d{2})(\d{2})(\d{2})(?!\d)", RegexOptions.CultureInvariant)] private static partial Regex Compact();
    [GeneratedRegex(@"(?<!\d)(\d{1,2})[/-](\d{1,2})[/-](\d{4}|\d{2})(?!\d)", RegexOptions.CultureInvariant)] private static partial Regex American();
    [GeneratedRegex(@"\b([A-Za-z]{3,9})\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})\b", RegexOptions.CultureInvariant)] private static partial Regex Written();
    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)?\s+([A-Za-z]{3,9})\.?,?\s+(\d{4})\b", RegexOptions.CultureInvariant)] private static partial Regex WrittenDayFirst();
}
