using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentStorage.Core;

// The details a folder and name are built from. Everything here is text the person approved
// (or will approve), never a path: each rendered segment is cleaned and validated on its own.
public sealed record SortValues(string Kind, string From, DateOnly? Date, string Title, IReadOnlyDictionary<string, string> Fields);

public static partial class SortPatterns
{
    public const int MaxPatternLength = 300;
    public static readonly string[] BuiltInTokens = ["kind", "from", "title", "date"];
    public static readonly string[] DateFormats = ["yyyy", "yyyy-MM", "yyyy-MM-dd", "MM", "MMMM"];

    public static string ValidatePattern(string pattern, bool folder)
    {
        pattern = (pattern ?? "").Trim().Replace('\\', '/').Trim('/');
        if (pattern.Length > MaxPatternLength) throw new StorageException("Keep a pattern under 300 characters.");
        if (!folder && pattern.Contains('/')) throw new StorageException("A name pattern cannot contain folders. Put folders in the folder pattern.");
        if (!folder && pattern.Length == 0) throw new StorageException("Enter a name pattern, such as {date} {from} {title}.");
        foreach (Match match in Brace().Matches(pattern))
            if (!Token().IsMatch(match.Value)) throw new StorageException($"{match.Value} is not a pattern token. Use {{kind}}, {{from}}, {{title}}, {{date}}, {{date:yyyy}} or a field such as {{invoice_number}}.");
        var sample = Token().Replace(pattern, "x");
        if (sample.Contains('{') || sample.Contains('}')) throw new StorageException("Close every { with }.");
        foreach (var segment in sample.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment.Trim() is ".." or ".") throw new StorageException("A pattern cannot step outside its folder.");
        return pattern;
    }

    public static string[] TokensIn(string pattern) =>
        Token().Matches(pattern ?? "").Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();

    // Folder segments, in order. A segment whose tokens are all empty is left out, so a missing
    // sender or date never makes a "Banking//2026" or an empty folder name.
    public static string[] Folder(string pattern, SortValues values)
    {
        var result = new List<string>();
        foreach (var raw in (pattern ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var rendered = Render(raw, values, out var hadToken, out var anyValue);
            if (hadToken && !anyValue) continue;
            var clean = Clean(rendered);
            if (clean.Length == 0) continue;
            result.Add(Safe(clean, "Folder"));
        }
        return result.ToArray();
    }

    public static string Name(string pattern, SortValues values, string originalName)
    {
        var extension = Path.GetExtension(originalName);
        var stem = Clean(Render(pattern ?? "", values, out _, out _));
        if (stem.Length == 0) stem = Clean(Path.GetFileNameWithoutExtension(originalName));
        if (stem.Length == 0) stem = "Document";
        var limit = 180 - extension.Length;
        if (stem.Length > limit) stem = stem[..limit].TrimEnd(' ', '.', '-', '_');
        return Safe(stem + extension, "Document" + extension);
    }

    private static string Render(string pattern, SortValues values, out bool hadToken, out bool anyValue)
    {
        var had = false; var any = false;
        var text = Token().Replace(pattern, match =>
        {
            had = true;
            var value = Value(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null, values);
            if (value.Length > 0) any = true;
            return value;
        });
        hadToken = had; anyValue = any;
        return text;
    }

    private static string Value(string token, string? format, SortValues values)
    {
        switch (token)
        {
            case "kind": return values.Kind;
            case "from": return values.From;
            case "title": return values.Title;
            case "date": return values.Date is DateOnly date ? date.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        }
        if (!values.Fields.TryGetValue(token, out var field) || string.IsNullOrWhiteSpace(field)) return "";
        if (format != null && DateOnly.TryParseExact(field.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fieldDate))
            return fieldDate.ToString(format, CultureInfo.InvariantCulture);
        return field;
    }

    // Model or person text becomes one portable path segment: separators and reserved
    // characters turn into dashes, runs of space collapse, and leading/trailing dots go.
    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsControl(c) || "/\\:*?\"<>|".Contains(c) ? '-' : c);
        var result = Spaces().Replace(builder.ToString(), " ");
        result = Dashes().Replace(result, " - ");
        result = Spaces().Replace(result, " ").Trim(' ', '.', '-', '_');
        // An empty token leaves dangling separators: "2026-10-14  - statement" or "( )".
        result = EmptyBrackets().Replace(result, "");
        result = Spaces().Replace(result, " ").Trim(' ', '.', '-', '_');
        return result;
    }

    private static string Safe(string name, string fallback)
    {
        try { return StoragePaths.ValidateName(name); }
        catch (StorageException)
        {
            try { return StoragePaths.ValidateName("_" + name); }
            catch (StorageException) { return fallback; }
        }
    }

    [GeneratedRegex(@"\{[^{}]*\}", RegexOptions.CultureInvariant)] private static partial Regex Brace();
    [GeneratedRegex(@"\{([a-z][a-z0-9_]{0,63})(?::(yyyy|yyyy-MM|yyyy-MM-dd|MM|MMMM))?\}", RegexOptions.CultureInvariant)] private static partial Regex Token();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Spaces();
    [GeneratedRegex(@"\s*-(\s*-)+\s*|\s+-\s*|\s*-\s+", RegexOptions.CultureInvariant)] private static partial Regex Dashes();
    [GeneratedRegex(@"\(\s*\)|\[\s*\]", RegexOptions.CultureInvariant)] private static partial Regex EmptyBrackets();
}
