using System.Text.RegularExpressions;

namespace ThinkTech.Storage.Api.Storage;

/// <summary>A file just written by <see cref="IFileStorage.SaveAsync"/>.</summary>
/// <param name="FilePath">
/// Path below the client's folder, <c>{filePartialPath}/{id}.{ext}</c>, e.g. <c>invoices/2026/11/{id}.jpg</c>.
/// It is the only thing needed to download or delete the file later.
/// </param>
/// <param name="FileName">Name on disk: <c>{id}.{ext}</c>.</param>
public sealed record StoredFile(
    string Id,
    string FilePath,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UploadedAtUtc);

/// <summary>An opened stored file. The caller owns (and must dispose) <see cref="Content"/>.</summary>
public sealed record StoredFileContent(
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    Stream Content);

public static partial class StorageIdentifiers
{
    /// <summary>Client ids double as folder names, so they are restricted to a safe, lowercase alphabet.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{2,63}$")]
    public static partial Regex ClientId();

    /// <summary>Extensions kept on stored files: short, lowercase letters and digits only (e.g. ".jpg", ".7z").</summary>
    [GeneratedRegex("^\\.[a-z0-9]{1,16}$")]
    public static partial Regex DataExtension();

    /// <summary>A stored file name: "{id}.{ext}", the id being a GUID in "N" format (32 lowercase hex characters).</summary>
    [GeneratedRegex("^[0-9a-f]{32}\\.[a-z0-9]{1,16}$")]
    public static partial Regex DataFileName();

    /// <summary>
    /// One folder name in a filePartialPath: letters, digits, spaces, '.', '-', '_'; it must start and end with a
    /// letter, digit, '-' or '_'. This rules out ".", "..", hidden folders and trailing dots.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_-]([A-Za-z0-9 ._-]{0,98}[A-Za-z0-9_-])?$")]
    public static partial Regex PathSegment();
}
