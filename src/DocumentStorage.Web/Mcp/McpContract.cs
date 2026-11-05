using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentStorage.Core;
using DocumentStorage.Data;

namespace DocumentStorage.Web.Mcp;

public sealed record McpRuleInput(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("expected_revision")] int ExpectedRevision,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("document_type")] string DocumentType,
    [property: JsonPropertyName("destination_folder_id")] Guid DestinationFolderId,
    [property: JsonPropertyName("match")] Dictionary<string,string> Match,
    [property: JsonPropertyName("filename_template")] string FilenameTemplate,
    [property: JsonPropertyName("required_facts")] string[] RequiredFacts,
    [property: JsonPropertyName("enabled")] bool Enabled)
{
    public RuleInput ToCore() => new(Id, ExpectedRevision, Name, DocumentType, DestinationFolderId, Match, FilenameTemplate, RequiredFacts, Enabled);
}
public static class McpContract
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static JsonElement OutputSchema { get; } = JsonDocument.Parse("""
        {"type":"object","required":["schema_version","ok"],"additionalProperties":false,
        "properties":{"schema_version":{"const":1},"ok":{"type":"boolean"},"data":{"type":"object"},
        "error":{"type":"object","required":["code","message"],"additionalProperties":false,"properties":{"code":{"type":"string"},"message":{"type":"string"}}}},
        "oneOf":[{"properties":{"ok":{"const":true}},"required":["data"]},{"properties":{"ok":{"const":false}},"required":["error"]}]}
        """).RootElement.Clone();
    public static object Proposal(AssistantProposal value, bool includeContent = false)
    {
        object? content = !includeContent ? null : value.Kind switch
        {
            "Guidance" => JsonSerializer.Deserialize<GuidanceChange>(value.PayloadJson),
            "Rule" => JsonSerializer.Deserialize<RuleInput>(value.PayloadJson),
            "Details" => JsonSerializer.Deserialize<DetailsProposal>(value.PayloadJson),
            "Kind" => JsonSerializer.Deserialize<KindProposal>(value.PayloadJson),
            "Merge" => JsonSerializer.Deserialize<MergeProposal>(value.PayloadJson),
            "Refile" => JsonSerializer.Deserialize<RefileProposal>(value.PayloadJson),
            _ => JsonSerializer.Deserialize<FilingRequest>(value.PayloadJson)
        };
        object? result = value.Kind == "Filing" && value.State != "Executed"
            ? JsonSerializer.Deserialize<FilingProposal>(value.ResultJson) : JsonSerializer.Deserialize<JsonElement>(value.ResultJson);
        return new { value.Id, value.Kind, value.State, value.Revision, value.CreatedAt, value.ExpiresAt,
            value.ContextHash, value.SourceRequest, ready_to_execute = value.State == "Approved" && value.Kind == "Filing", content, result };
    }
}
