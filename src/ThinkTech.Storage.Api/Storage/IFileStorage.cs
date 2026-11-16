namespace ThinkTech.Storage.Api.Storage;

/// <summary>
/// Storage backend. Every operation is scoped to a client, so one client can never reach another client's files.
/// Only the uploaded file itself is stored; there is no metadata file. The default implementation writes to the
/// server's local disk; swap it for S3/MinIO/Azure Blob later if needed.
/// </summary>
public interface IFileStorage
{
    /// <param name="filePartialPath">Folder below the client's folder, already normalized by <see cref="FilePartialPathParser"/>; empty for the client's root folder.</param>
    /// <param name="extension">Lowercase extension including the dot, e.g. ".jpg". Anything unusual is stored as ".bin".</param>
    Task<StoredFile> SaveAsync(
        string clientId, Stream content, string filePartialPath, string extension, CancellationToken cancellationToken);

    /// <summary>
    /// Opens <c>{filePartialPath}/{id}.{ext}</c> as returned by <see cref="SaveAsync"/>. Returns null when the path is
    /// invalid or the file does not exist. The caller owns (and must dispose) the returned stream.
    /// </summary>
    Task<StoredFileContent?> OpenReadAsync(string clientId, string filePath, CancellationToken cancellationToken);

    /// <summary>Returns false when the path is invalid or the file does not exist.</summary>
    Task<bool> DeleteAsync(string clientId, string filePath, CancellationToken cancellationToken);
}
