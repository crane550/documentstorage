using DocumentStorage.Core;

namespace DocumentStorage.Web;

public sealed class FactField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    public static Dictionary<string,string> Read(IEnumerable<FactField> fields)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
        {
            if (!result.TryAdd(field.Name.Trim(), field.Value.Trim())) throw new StorageException("Each fact name must appear only once.");
        }
        return result;
    }

    public static List<FactField> From(Dictionary<string,string> values) => values.Select(x => new FactField { Name = x.Key, Value = x.Value }).ToList();
}
