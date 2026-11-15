using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ThinkTech.Storage.Api.Authentication;
using ThinkTech.Storage.Api.Configuration;
using ThinkTech.Storage.Api.Contracts;
using ThinkTech.Storage.Api.Storage;

namespace ThinkTech.Storage.Api.Controllers;

/// <summary>Upload, download, delete and inspect documents. Each client only ever sees its own files.</summary>
[ApiController]
[Route("api/v1/files")]
[Authorize]
public sealed class FilesController(
    IFileStorage storage,
    IOptionsMonitor<StorageOptions> storageOptions,
    ILogger<FilesController> logger) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    [HttpPost]
    [Authorize(Policy = StoragePolicies.Write)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> Upload([FromForm] UploadFileRequest request, CancellationToken cancellationToken)
    {
        var options = storageOptions.CurrentValue;
        var file = request.File!;

        if (file.Length == 0)
        {
            return InvalidField(nameof(UploadFileRequest.File), "The file is empty.");
        }

        if (file.Length > options.MaxFileSizeBytes)
        {
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large",
                detail: $"The maximum file size is {options.MaxFileSizeBytes:N0} bytes.");
        }

        var fileName = FileNameSanitizer.Sanitize(file.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (options.AllowedExtensions.Length > 0 &&
            !options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "File type not allowed",
                detail: $"Files of type '{extension}' are not accepted.");
        }

        if (!UploadMetadataParser.TryParse(request.Metadata, out var metadata, out var metadataError))
        {
            return InvalidField(nameof(UploadFileRequest.Metadata), metadataError!);
        }

        if (!FilePartialPathParser.TryNormalize(request.FilePartialPath, out var partialPath, out var partialPathError))
        {
            return InvalidField(nameof(UploadFileRequest.FilePartialPath), partialPathError!);
        }

        // Content type comes from the extension, not from the caller, so a client cannot label a file as something else.
        var contentType = ContentTypes.TryGetContentType(fileName, out var detected) ? detected : "application/octet-stream";
        var clientId = User.GetClientId();

        await using var content = file.OpenReadStream();
        var stored = await storage.SaveAsync(
            clientId,
            content,
            new FileUploadInfo(partialPath, fileName, contentType, Normalize(request.Category), Normalize(request.ReferenceId), metadata),
            cancellationToken);

        logger.LogInformation("Client {ClientId} uploaded file {StoredFileName} to '{FilePartialPath}' ({FileName}, {SizeBytes} bytes)",
            clientId, stored.StoredFileName, stored.FilePartialPath ?? string.Empty, stored.FileName, stored.SizeBytes);

        return CreatedAtAction(nameof(GetMetadata), new { file = stored.StoredFileName }, FileMetadataResponse.From(stored));
    }

    /// <summary>Downloads a file. <paramref name="file"/> is the <c>storedFileName</c> from the upload, e.g. <c>{id}.jpg</c> (the bare id works too).</summary>
    [HttpGet("{file}")]
    [Authorize(Policy = StoragePolicies.Read)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string file, CancellationToken cancellationToken)
    {
        if (!TryParseFileReference(file, out var fileId, out var extension))
        {
            return NotFound();
        }

        var clientId = User.GetClientId();
        var result = await storage.OpenReadAsync(clientId, fileId, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        var (metadata, content) = result;
        if (!ExtensionMatches(metadata, extension))
        {
            await content.DisposeAsync();
            return NotFound();
        }

        logger.LogInformation("Client {ClientId} downloaded file {StoredFileName}", clientId, metadata.StoredFileName);

        // FileStreamResult disposes the stream; range requests allow resumable downloads.
        return File(content, metadata.ContentType, metadata.FileName, metadata.UploadedAtUtc,
            new EntityTagHeaderValue($"\"{metadata.Sha256}\""), enableRangeProcessing: true);
    }

    [HttpGet("{file}/metadata")]
    [Authorize(Policy = StoragePolicies.Read)]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMetadata(string file, CancellationToken cancellationToken)
    {
        if (!TryParseFileReference(file, out var fileId, out var extension))
        {
            return NotFound();
        }

        var metadata = await storage.GetMetadataAsync(User.GetClientId(), fileId, cancellationToken);
        return metadata is not null && ExtensionMatches(metadata, extension)
            ? Ok(FileMetadataResponse.From(metadata))
            : NotFound();
    }

    [HttpDelete("{file}")]
    [Authorize(Policy = StoragePolicies.Delete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string file, CancellationToken cancellationToken)
    {
        if (!TryParseFileReference(file, out var fileId, out var extension))
        {
            return NotFound();
        }

        var clientId = User.GetClientId();
        var metadata = await storage.GetMetadataAsync(clientId, fileId, cancellationToken);
        if (metadata is null || !ExtensionMatches(metadata, extension) ||
            !await storage.DeleteAsync(clientId, fileId, cancellationToken))
        {
            return NotFound();
        }

        logger.LogInformation("Client {ClientId} deleted file {StoredFileName} from '{FilePartialPath}'",
            clientId, metadata.StoredFileName, metadata.FilePartialPath ?? string.Empty);
        return NoContent();
    }

    /// <summary>
    /// Parses the <c>{file}</c> route value: <c>{id}.{ext}</c> as returned in <c>storedFileName</c>
    /// (e.g. <c>7964b4d03c184c57b397a16490bef832.jpg</c>), or the bare 32-character id.
    /// </summary>
    private static bool TryParseFileReference(string file, out string fileId, out string? extension)
    {
        fileId = string.Empty;
        extension = null;

        if (file.Length < 32)
        {
            return false;
        }

        var id = file[..32].ToLowerInvariant();
        var rest = file[32..];
        if (!StorageIdentifiers.FileId().IsMatch(id) || (rest.Length > 0 && rest[0] != '.'))
        {
            return false;
        }

        fileId = id;
        extension = rest.Length > 0 ? rest : null;
        return true;
    }

    /// <summary>A given extension must match the stored file (".jpg") or the original file name; otherwise it's a 404.</summary>
    private static bool ExtensionMatches(StoredFileMetadata metadata, string? extension) =>
        extension is null ||
        string.Equals(extension, Path.GetExtension(metadata.StoredFileName), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, Path.GetExtension(metadata.FileName), StringComparison.OrdinalIgnoreCase);

    private IActionResult InvalidField(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
