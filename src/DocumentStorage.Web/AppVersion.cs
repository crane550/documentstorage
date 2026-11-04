using System.Reflection;
using System.Text.RegularExpressions;

namespace DocumentStorage.Web;

public static class AppVersion
{
    private static readonly Assembly Assembly = typeof(AppVersion).Assembly;
    private static string? Metadata(string key) => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == key)?.Value;
    public static string Build { get; } = (Metadata("BuildNumber") ?? "0").PadLeft(3, '0');
    public static string Display { get; } = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "development";
    public static string? Commit { get; } = ValidCommit(Metadata("RepositoryCommit")) ? Metadata("RepositoryCommit") : null;
    public static string ShortCommit => Commit?[..8] ?? "development";
    public static bool ValidCommit(string? value) => value != null && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
}
