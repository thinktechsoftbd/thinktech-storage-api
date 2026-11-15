using System.Text.RegularExpressions;

namespace ThinkTech.Storage.Api.Storage;

/// <summary>Metadata saved for every stored file, in <c>{clientId}/.metadata/{id[0..2]}/{id[2..4]}/{id}.json</c>.</summary>
public sealed class StoredFileMetadata
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }

    /// <summary>Name of the content file on disk: <c>{id}.{ext}</c> (e.g. <c>{id}.jpg</c>).</summary>
    public required string StoredFileName { get; init; }

    /// <summary>Normalized folder below the client's folder, e.g. <c>invoices/2026/11</c>; null for the client's root folder.</summary>
    public string? FilePartialPath { get; init; }

    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public string? Category { get; init; }
    public string? ReferenceId { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
    public required DateTimeOffset UploadedAtUtc { get; init; }
}

/// <param name="FilePartialPath">Already normalized by <see cref="FilePartialPathParser"/>; empty for the client's root folder.</param>
public sealed record FileUploadInfo(
    string FilePartialPath,
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

    /// <summary>Extensions kept on stored data files: short, lowercase letters and digits only (e.g. ".jpg", ".7z").</summary>
    [GeneratedRegex("^\\.[a-z0-9]{1,16}$")]
    public static partial Regex DataExtension();

    /// <summary>A data file name produced by the storage: "{id}.{ext}".</summary>
    [GeneratedRegex("^[0-9a-f]{32}\\.[a-z0-9]{1,16}$")]
    public static partial Regex DataFileName();

    /// <summary>
    /// One folder name in a filePartialPath: letters, digits, spaces, '.', '-', '_'; it must start and end with a
    /// letter, digit, '-' or '_'. This rules out ".", "..", hidden folders (such as ".metadata") and trailing dots.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_-]([A-Za-z0-9 ._-]{0,98}[A-Za-z0-9_-])?$")]
    public static partial Regex PathSegment();
}
