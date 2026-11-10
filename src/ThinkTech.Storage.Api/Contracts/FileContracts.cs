using System.ComponentModel.DataAnnotations;
using ThinkTech.Storage.Api.Storage;

namespace ThinkTech.Storage.Api.Contracts;

/// <summary>multipart/form-data body for <c>POST /api/v1/files</c>.</summary>
public sealed class UploadFileRequest
{
    [Required]
    public IFormFile? File { get; set; }

    /// <summary>Optional free-text grouping, e.g. "invoices", "patient-reports".</summary>
    [MaxLength(100)]
    public string? Category { get; set; }

    /// <summary>Optional id from the calling system, e.g. an invoice number or patient id.</summary>
    [MaxLength(200)]
    public string? ReferenceId { get; set; }

    /// <summary>Optional JSON object of string key/value pairs, e.g. <c>{"invoiceNo":"INV-1001"}</c>.</summary>
    [MaxLength(8192)]
    public string? Metadata { get; set; }
}

public sealed record FileMetadataResponse(
    string Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? Category,
    string? ReferenceId,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset UploadedAtUtc)
{
    public static FileMetadataResponse From(StoredFileMetadata file) => new(
        file.Id,
        file.FileName,
        file.ContentType,
        file.SizeBytes,
        file.Sha256,
        file.Category,
        file.ReferenceId,
        file.Metadata,
        file.UploadedAtUtc);
}
