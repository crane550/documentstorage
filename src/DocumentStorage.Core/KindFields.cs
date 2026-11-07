using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;

namespace DocumentStorage.Core;

// Configure by using: a field is first typed on one document's slip, then kept for its kind with one
// click, and put into the kind's name or folder with another. People write "Statement beginning
// date"; patterns and the reader use the key statement_beginning_date.
public sealed partial class LibraryService
{
    public const int KindFieldLimit = 12;

    public static string FieldKey(string label)
    {
        var key = NonWord().Replace(label.Trim().ToLowerInvariant(), "_").Trim('_');
        while (key.Contains("__")) key = key.Replace("__", "_");
        if (key.Length > 0 && char.IsDigit(key[0])) key = "f_" + key;
        if (key.Length > 64) key = key[..64].TrimEnd('_');
        return FactKey(key.Length == 0 ? "?" : key);
    }

    public static string FieldLabel(string key)
    {
        var words = key.Replace('_', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    // A field named with the word "date" holds a date, kept as yyyy-MM-dd so patterns can format it.
    public static bool IsDateField(string key) => key.Split('_').Contains("date");

    // Puts a typed date value into yyyy-MM-dd when it reads as one; anything else is left as typed.
    public static string NormalizeFieldValue(string key, string value)
    {
        value = value.Trim();
        if (!IsDateField(key) || value.Length == 0) return value;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", out _)) return value;
        return DocumentHints.FindDates(value, "value").FirstOrDefault()?.Date.ToString("yyyy-MM-dd") ?? value;
    }

    public static bool FieldInPattern(DocumentKind kind, string field, bool folder) =>
        SortPatterns.TokensIn(folder ? kind.FolderPattern : kind.NamePattern).Contains(field);

    public async Task<DocumentKind> AddKindFieldAsync(string kindName, string field, string actor)
    {
        Actor(actor);
        var key = FactKey(field);
        if (ReservedAiFacts.Contains(key)) throw new StorageException("That name is reserved. Choose another field name.");
        return await ChangeKindAsync(kindName, kind =>
        {
            var fields = KindFields(kind).ToList();
            if (fields.Contains(key)) return false;
            if (fields.Count >= KindFieldLimit) throw new StorageException($"A kind can carry up to {KindFieldLimit} fields. Remove one on the Kinds page first.");
            fields.Add(key); kind.FieldsJson = JsonSerializer.Serialize(fields);
            return true;
        });
    }

    // Adds or removes a field's token: at the end of the name, or as the last folder level (a date
    // field files by its year there; a folder per day would be too many).
    public async Task<DocumentKind> SetFieldInPatternAsync(string kindName, string field, bool folder, bool on, string actor)
    {
        Actor(actor);
        var key = FactKey(field);
        return await ChangeKindAsync(kindName, kind =>
        {
            if (FieldInPattern(kind, key, folder) == on) return false;
            if (on && !KindFields(kind).Contains(key))
            {
                var fields = KindFields(kind).ToList();
                if (fields.Count >= KindFieldLimit) throw new StorageException($"A kind can carry up to {KindFieldLimit} fields.");
                fields.Add(key); kind.FieldsJson = JsonSerializer.Serialize(fields);
            }
            var token = "{" + key + (folder && GuideFor(kind, key).Type == "date" ? ":yyyy" : "") + "}";
            if (folder)
            {
                var segments = kind.FolderPattern.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (on) segments.Add(token);
                else segments = segments.Select(x => TokenOf(key).Replace(x, "").Trim()).Where(x => x.Length > 0).ToList();
                kind.FolderPattern = SortPatterns.ValidatePattern(string.Join('/', segments), folder: true);
            }
            else
            {
                var pattern = on ? (kind.NamePattern + " " + token).Trim() : TokenOf(key).Replace(kind.NamePattern, "");
                pattern = Regex.Replace(pattern, @"\s+", " ").Trim();
                if (pattern.Length == 0) pattern = "{title}";
                kind.NamePattern = SortPatterns.ValidatePattern(pattern, folder: false);
            }
            return true;
        });
    }

    private async Task<DocumentKind> ChangeKindAsync(string kindName, Func<DocumentKind, bool> change)
    {
        await writer.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var kind = MatchKind(kindName, await db.Kinds.ToListAsync()) ?? throw new StorageException("Approve the first document of this kind before changing it; its fields travel with that document until then.");
            if (change(kind)) { kind.Revision++; await db.SaveChangesAsync(); }
            return kind;
        }
        finally { writer.Release(); }
    }

    private static Regex TokenOf(string key) => new(@"\{" + Regex.Escape(key) + @"(:[^}]*)?\}", RegexOptions.CultureInvariant);
    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)] private static partial Regex NonWord();
}
