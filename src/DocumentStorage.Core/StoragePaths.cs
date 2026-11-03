namespace DocumentStorage.Core;

public sealed class StorageException(string message) : Exception(message);

public static class StoragePaths
{
    public const string Marker = ".documentstorage-root";
    public const string Trash = ".documentstorage-trash";
    public const string Staging = ".documentstorage-staging";
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string ValidateName(string value)
    {
        var name = value.Trim();
        // Portable Windows/NAS names, regardless of the host OS.
        if (name.Length is 0 or > 180 || name is "." or ".." || name.EndsWith('.') ||
            name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.Any(char.IsControl) ||
            name.StartsWith(".documentstorage", StringComparison.OrdinalIgnoreCase))
            throw new StorageException("Use a name without slashes, reserved characters, or a trailing period (180 characters maximum).");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
            throw new StorageException("That name is reserved by Windows. Choose another name.");
        return name;
    }

    public static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(x => x is "." or ".."))
            throw new StorageException("The path must stay inside its storage location.");
        var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(basePath) ? basePath : basePath + Path.DirectorySeparatorChar;
        if (!full.Equals(basePath, Comparison) && !full.StartsWith(prefix, Comparison))
            throw new StorageException("The path must stay inside its storage location.");
        // Refuse reparse points in the root, its ancestors, and all descendant components.
        for (var part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
        {
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new StorageException("Symbolic links and junctions are not managed. Choose a physical folder.");
        }
        return full;
    }

    public static bool IsInside(string candidate, string parent) => candidate.Equals(parent, Comparison) ||
        candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);
    public static string Join(string parent, string name) => string.IsNullOrEmpty(parent) ? name : parent + "/" + name;
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
