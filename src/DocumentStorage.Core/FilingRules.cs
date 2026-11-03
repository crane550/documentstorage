using System.Globalization;
using System.Text.RegularExpressions;

namespace DocumentStorage.Core;

// Versioned, data-only contract for future Ollama/MCP integration. It never executes code.
public sealed record FilingRule(Guid Id, string DocumentType, Guid DestinationFolderId,
    Dictionary<string, string> Match, string FilenameTemplate, string[] RequiredFacts);
public sealed record DocumentFacts(string DocumentType, bool NeedsReview, Dictionary<string, string> Values);
public sealed record FilingPlan(bool Ready, string Reason, Guid? FolderId = null, string? Filename = null, Guid? RuleId = null);

public static partial class FilingRules
{
    public static FilingPlan Preview(DocumentFacts facts, IEnumerable<FilingRule> rules)
    {
        if (facts.NeedsReview) return new(false, "Document identification needs review.");
        var candidates = rules.Where(rule => string.Equals(rule.DocumentType, facts.DocumentType, StringComparison.OrdinalIgnoreCase) &&
            rule.Match.All(condition => facts.Values.TryGetValue(condition.Key, out var value) && string.Equals(value, condition.Value, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count != 1) return new(false, candidates.Count == 0 ? "No filing rule matched." : "Several rules matched. Review the conflict.");
        var rule = candidates[0];
        foreach (var key in rule.RequiredFacts)
            if (!facts.Values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return new(false, $"Required fact is missing: {key}.");
        try
        {
            var name = Token().Replace(rule.FilenameTemplate, match =>
            {
                var key = match.Groups[1].Value;
                if (!facts.Values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) throw new StorageException($"Required fact is missing: {key}.");
                if (!match.Groups[2].Success) return value;
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new StorageException($"{key} must be an ISO date (yyyy-MM-dd).");
                return date.ToString(match.Groups[2].Value, CultureInfo.InvariantCulture);
            });
            if (name.Contains('{') || name.Contains('}')) throw new StorageException("The filename template contains an unsupported token.");
            name = StoragePaths.ValidateName(name);
            return new(true, "One rule matched. Destination availability and collisions must be checked before execution.", rule.DestinationFolderId, name, rule.Id);
        }
        catch (StorageException ex) { return new(false, ex.Message); }
    }

    [GeneratedRegex(@"\{([a-z_]+)(?::(yyyy-MM-dd|yyyy-MM|yyyy))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
