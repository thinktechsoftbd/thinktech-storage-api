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

        // Content type comes from the extension, not from the caller, so a client cannot label a file as something else.
        var contentType = ContentTypes.TryGetContentType(fileName, out var detected) ? detected : "application/octet-stream";
        var clientId = User.GetClientId();

        await using var content = file.OpenReadStream();
        var stored = await storage.SaveAsync(
            clientId,
            content,
            new FileUploadInfo(fileName, contentType, Normalize(request.Category), Normalize(request.ReferenceId), metadata),
            cancellationToken);

        logger.LogInformation("Client {ClientId} uploaded file {FileId} ({FileName}, {SizeBytes} bytes)",
            clientId, stored.Id, stored.FileName, stored.SizeBytes);

        return CreatedAtAction(nameof(GetMetadata), new { id = stored.Id }, FileMetadataResponse.From(stored));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = StoragePolicies.Read)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var clientId = User.GetClientId();
        var result = await storage.OpenReadAsync(clientId, id.ToString("N"), cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        var (metadata, content) = result;
        logger.LogInformation("Client {ClientId} downloaded file {FileId}", clientId, metadata.Id);

        // FileStreamResult disposes the stream; range requests allow resumable downloads.
        return File(content, metadata.ContentType, metadata.FileName, metadata.UploadedAtUtc,
            new EntityTagHeaderValue($"\"{metadata.Sha256}\""), enableRangeProcessing: true);
    }

    [HttpGet("{id:guid}/metadata")]
    [Authorize(Policy = StoragePolicies.Read)]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMetadata(Guid id, CancellationToken cancellationToken)
    {
        var metadata = await storage.GetMetadataAsync(User.GetClientId(), id.ToString("N"), cancellationToken);
        return metadata is null ? NotFound() : Ok(FileMetadataResponse.From(metadata));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = StoragePolicies.Delete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var clientId = User.GetClientId();
        var fileId = id.ToString("N");

        if (!await storage.DeleteAsync(clientId, fileId, cancellationToken))
        {
            return NotFound();
        }

        logger.LogInformation("Client {ClientId} deleted file {FileId}", clientId, fileId);
        return NoContent();
    }

    private IActionResult InvalidField(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
