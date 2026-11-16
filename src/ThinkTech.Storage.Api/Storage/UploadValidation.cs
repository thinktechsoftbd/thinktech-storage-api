using System.Text;

namespace ThinkTech.Storage.Api.Storage;

public static class FileNameSanitizer
{
    private const int MaxLength = 200;
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Cleans the client-supplied file name. It is only kept as metadata (for the download's
    /// Content-Disposition), never used as a path on disk.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        // Strip any directory part, treating both '/' and '\' as separators regardless of the server OS.
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsControl(ch) || InvalidChars.Contains(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        if (sanitized.Length == 0)
        {
            return "file";
        }

        if (sanitized.Length > MaxLength)
        {
            var extension = Path.GetExtension(sanitized);
            if (extension.Length > 20)
            {
                extension = string.Empty;
            }

            sanitized = sanitized[..(MaxLength - extension.Length)] + extension;
        }

        return sanitized;
    }
}

/// <summary>
/// Validates and normalizes the caller's <c>filePartialPath</c>, e.g. <c>"\invoices\2026//11/"</c> becomes
/// <c>"invoices/2026/11"</c>. Only safe folder names are accepted, so the path can never leave the client's folder.
/// </summary>
public static class FilePartialPathParser
{
    private const int MaxLength = 255;
    private const int MaxSegments = 10;

    public static bool TryNormalize(string? value, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var segments = value.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length > MaxSegments)
        {
            error = $"filePartialPath can contain at most {MaxSegments} folders.";
            return false;
        }

        if (!segments.All(s => StorageIdentifiers.PathSegment().IsMatch(s)))
        {
            error = "filePartialPath folder names may contain letters, digits, spaces, '.', '-' and '_' (max 100 characters), " +
                    "must start and end with a letter, digit, '-' or '_', and cannot be '.' or '..'. Example: invoices/2026/11";
            return false;
        }

        var joined = string.Join('/', segments);
        if (joined.Length > MaxLength)
        {
            error = $"filePartialPath can be at most {MaxLength} characters.";
            return false;
        }

        normalized = joined;
        return true;
    }
}

/// <summary>
/// Parses a <c>filePath</c> returned by an upload, e.g. <c>invoices/2026/11/7964b4d03c184c57b397a16490bef832.jpg</c>,
/// into its folder and file name. Only paths the storage could have produced are accepted.
/// </summary>
public static class StoredFilePathParser
{
    public static bool TryParse(string? value, out string filePartialPath, out string fileName)
    {
        filePartialPath = string.Empty;
        fileName = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var path = value.Replace('\\', '/').Trim('/');
        var lastSlash = path.LastIndexOf('/');
        var name = path[(lastSlash + 1)..].ToLowerInvariant(); // stored names are always lowercase
        var folder = lastSlash < 0 ? string.Empty : path[..lastSlash];

        if (!StorageIdentifiers.DataFileName().IsMatch(name) ||
            !FilePartialPathParser.TryNormalize(folder, out var normalizedFolder, out _))
        {
            return false;
        }

        filePartialPath = normalizedFolder;
        fileName = name;
        return true;
    }
}
