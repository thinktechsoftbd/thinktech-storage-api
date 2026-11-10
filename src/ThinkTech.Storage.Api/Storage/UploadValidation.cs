using System.Text;
using System.Text.Json;

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

public static class UploadMetadataParser
{
    private const int MaxEntries = 32;
    private const int MaxKeyLength = 64;
    private const int MaxValueLength = 1024;

    public static bool TryParse(string? json, out Dictionary<string, string> metadata, out string? error)
    {
        metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            error = "Metadata must be a JSON object with string values, e.g. {\"invoiceNo\":\"INV-1001\"}.";
            return false;
        }

        if (parsed is null)
        {
            return true;
        }

        if (parsed.Count > MaxEntries)
        {
            error = $"Metadata can contain at most {MaxEntries} entries.";
            return false;
        }

        foreach (var (key, value) in parsed)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
            {
                error = $"Metadata keys must be 1-{MaxKeyLength} characters.";
                return false;
            }

            if (value is null || value.Length > MaxValueLength)
            {
                error = $"Metadata values must be non-null and at most {MaxValueLength} characters.";
                return false;
            }

            metadata[key] = value;
        }

        return true;
    }
}
