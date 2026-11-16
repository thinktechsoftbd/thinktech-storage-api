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

/// <summary>
/// Upload, download and delete documents. Only the file itself is stored, at
/// <c>{RootPath}/{clientId}/{filePartialPath}/{id}.{ext}</c>. Each client only ever sees its own files.
/// </summary>
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
    [ProducesResponseType<UploadFileResponse>(StatusCodes.Status201Created)]
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

        var originalFileName = FileNameSanitizer.Sanitize(file.FileName);
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (options.AllowedExtensions.Length > 0 &&
            !options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "File type not allowed",
                detail: $"Files of type '{extension}' are not accepted.");
        }

        if (!FilePartialPathParser.TryNormalize(request.FilePartialPath, out var partialPath, out var partialPathError))
        {
            return InvalidField(nameof(UploadFileRequest.FilePartialPath), partialPathError!);
        }

        var clientId = User.GetClientId();

        await using var content = file.OpenReadStream();
        var stored = await storage.SaveAsync(clientId, content, partialPath, extension, cancellationToken);

        logger.LogInformation("Client {ClientId} uploaded {FilePath} ({OriginalFileName}, {SizeBytes} bytes)",
            clientId, stored.FilePath, originalFileName, stored.SizeBytes);

        return CreatedAtAction(nameof(Download), new { filePath = stored.FilePath },
            UploadFileResponse.From(stored, originalFileName, GetContentType(stored.FileName)));
    }

    /// <summary>
    /// Downloads a file. <paramref name="filePath"/> is the <c>filePath</c> returned by the upload,
    /// e.g. <c>invoices/2026/11/7964b4d0...832.jpg</c>. HEAD returns only the headers (size, type, last modified).
    /// </summary>
    [HttpGet("{**filePath}")]
    [HttpHead("{**filePath}")]
    [Authorize(Policy = StoragePolicies.Read)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string? filePath, CancellationToken cancellationToken)
    {
        var clientId = User.GetClientId();
        var file = await storage.OpenReadAsync(clientId, filePath ?? string.Empty, cancellationToken);
        if (file is null)
        {
            return NotFound();
        }

        if (HttpMethods.IsGet(Request.Method))
        {
            logger.LogInformation("Client {ClientId} downloaded {FilePath}", clientId, file.FilePath);
        }

        // FileStreamResult disposes the stream; range requests allow resumable downloads.
        var etag = new EntityTagHeaderValue($"\"{file.SizeBytes:x}-{file.LastModifiedUtc.UtcTicks:x}\"");
        return File(file.Content, GetContentType(file.FileName), file.FileName, file.LastModifiedUtc, etag,
            enableRangeProcessing: true);
    }

    /// <summary>Deletes a file. <paramref name="filePath"/> is the <c>filePath</c> returned by the upload.</summary>
    [HttpDelete("{**filePath}")]
    [Authorize(Policy = StoragePolicies.Delete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string? filePath, CancellationToken cancellationToken)
    {
        var clientId = User.GetClientId();
        if (!await storage.DeleteAsync(clientId, filePath ?? string.Empty, cancellationToken))
        {
            return NotFound();
        }

        logger.LogInformation("Client {ClientId} deleted {FilePath}", clientId, filePath);
        return NoContent();
    }

    // Content type comes from the extension, not from the caller, so a client cannot label a file as something else.
    private static string GetContentType(string fileName) =>
        ContentTypes.TryGetContentType(fileName, out var contentType) ? contentType : "application/octet-stream";

    private IActionResult InvalidField(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }
}
