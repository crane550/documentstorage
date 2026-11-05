using System.Text.RegularExpressions;

namespace DocumentStorage.Core;

// A pattern as chips: a token ({from}, {date:yyyy}, {account_number}) or a piece of plain text.
// The arranger edits chips; patterns are what is saved. Parse and Join round-trip.
public sealed record PatternPart(string Token, string? Format, string Text)
{
    public bool IsToken => Token.Length > 0;
    public string Pattern => IsToken ? "{" + Token + (Format == null ? "" : ":" + Format) + "}" : Text;
    public static PatternPart Of(string token, string? format = null) => new(token, format, "");
    public static PatternPart Words(string text) => new("", null, text);
}

public static partial class PatternParts
{
    // Date formats a date chip cycles through: full date, year-month, year, month name.
    public static readonly string?[] DateFormats = [null, "yyyy-MM", "yyyy", "MMMM"];

    public static List<PatternPart> Parse(string pattern)
    {
        var parts = new List<PatternPart>();
        var at = 0;
        foreach (Match match in Token().Matches(pattern ?? ""))
        {
            AddText(parts, pattern![at..match.Index]);
            parts.Add(PatternPart.Of(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null));
            at = match.Index + match.Length;
        }
        AddText(parts, (pattern ?? "")[at..]);
        return parts;
    }

    private static void AddText(List<PatternPart> parts, string text)
    {
        text = text.Trim();
        if (text.Length > 0) parts.Add(PatternPart.Words(text));
    }

    public static List<List<PatternPart>> ParseFolder(string pattern) =>
        (pattern ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Parse).Where(x => x.Count > 0).ToList();

    public static string Join(IEnumerable<PatternPart> parts) =>
        string.Join(' ', parts.Select(x => x.IsToken ? x.Pattern : x.Text.Trim().Replace('/', '-').Replace('{', '(').Replace('}', ')')).Where(x => x.Length > 0));

    public static string JoinFolder(IEnumerable<IEnumerable<PatternPart>> levels) =>
        string.Join('/', levels.Select(Join).Where(x => x.Length > 0));

    public static bool IsDateToken(string token) => token == "date" || LibraryService.IsDateField(token);

    public static string? NextFormat(string? format)
    {
        var index = Array.IndexOf(DateFormats, format);
        return DateFormats[(index + 1) % DateFormats.Length];
    }

    public static string Label(PatternPart part)
    {
        if (!part.IsToken) return part.Text;
        var name = part.Token switch { "kind" => "Kind", "from" => "From", "title" => "Title", "date" => "Date", _ => LibraryService.FieldLabel(part.Token) };
        return part.Format switch { "yyyy" => name + " (year)", "yyyy-MM" => name + " (year-month)", "MMMM" => name + " (month)", "MM" => name + " (month no.)", _ => name };
    }

    // What the chip comes to for this document, for showing beside its label.
    public static string Sample(PatternPart part, SortValues values) =>
        part.IsToken ? SortPatterns.Folder(part.Pattern, values).FirstOrDefault() ?? "" : part.Text;

    [GeneratedRegex(@"\{([a-z][a-z0-9_]{0,63})(?::(yyyy|yyyy-MM|yyyy-MM-dd|MM|MMMM))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
