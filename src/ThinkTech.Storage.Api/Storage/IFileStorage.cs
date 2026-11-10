namespace ThinkTech.Storage.Api.Storage;

/// <summary>
/// Storage backend. Every operation is scoped to a client, so one client can never reach another client's files.
/// The default implementation writes to the server's local disk; swap it for S3/MinIO/Azure Blob later if needed.
/// </summary>
public interface IFileStorage
{
    Task<StoredFileMetadata> SaveAsync(string clientId, Stream content, FileUploadInfo upload, CancellationToken cancellationToken);

    Task<StoredFileMetadata?> GetMetadataAsync(string clientId, string fileId, CancellationToken cancellationToken);

    /// <summary>Returns null when the file does not exist. The caller owns (and must dispose) the returned stream.</summary>
    Task<StoredFileContent?> OpenReadAsync(string clientId, string fileId, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string clientId, string fileId, CancellationToken cancellationToken);
}
