using System.Text.RegularExpressions;

namespace ThinkTech.Storage.Api.Storage;

/// <summary>Metadata saved next to every stored file (as <c>{id}.json</c>).</summary>
public sealed class StoredFileMetadata
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public string? Category { get; init; }
    public string? ReferenceId { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
    public required DateTimeOffset UploadedAtUtc { get; init; }
}

public sealed record FileUploadInfo(
    string FileName,
    string ContentType,
    string? Category,
    string? ReferenceId,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record StoredFileContent(StoredFileMetadata Metadata, Stream Content);

public static partial class StorageIdentifiers
{
    /// <summary>Client ids double as folder names, so they are restricted to a safe, lowercase alphabet.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{2,63}$")]
    public static partial Regex ClientId();

    /// <summary>File ids are server-generated GUIDs in "N" format (32 lowercase hex characters).</summary>
    [GeneratedRegex("^[0-9a-f]{32}$")]
    public static partial Regex FileId();
}
