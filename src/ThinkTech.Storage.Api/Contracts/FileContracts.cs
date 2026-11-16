using System.ComponentModel.DataAnnotations;
using ThinkTech.Storage.Api.Storage;

namespace ThinkTech.Storage.Api.Contracts;

/// <summary>multipart/form-data body for <c>POST /api/v1/files</c>.</summary>
public sealed class UploadFileRequest
{
    [Required]
    public IFormFile? File { get; set; }

    /// <summary>
    /// Optional folder inside the client's storage folder, e.g. <c>invoices/2026/11</c>. The file is stored at
    /// <c>{RootPath}/{clientId}/{filePartialPath}/{id}.{ext}</c>; when omitted, directly in the client's folder.
    /// </summary>
    [MaxLength(255)]
    public string? FilePartialPath { get; set; }
}

/// <summary>Returned by <c>POST /api/v1/files</c>. Save <see cref="FilePath"/>: GET and DELETE need it.</summary>
/// <param name="FilePath">
/// <c>{filePartialPath}/{id}.{ext}</c>, e.g. <c>invoices/2026/11/7964b4d0...832.jpg</c>.
/// Use it as <c>GET/DELETE /api/v1/files/{FilePath}</c>.
/// </param>
/// <param name="FileName">Name on disk, <c>{id}.{ext}</c>. Downloads are served under this name.</param>
/// <param name="OriginalFileName">The name the file was uploaded with. It is not stored on the server.</param>
public sealed record UploadFileResponse(
    string Id,
    string FilePath,
    string FileName,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UploadedAtUtc)
{
    public static UploadFileResponse From(StoredFile file, string originalFileName, string contentType) => new(
        file.Id,
        file.FilePath,
        file.FileName,
        originalFileName,
        contentType,
        file.SizeBytes,
        file.Sha256,
        file.UploadedAtUtc);
}
