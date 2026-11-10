using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ThinkTech.Storage.Api.Configuration;

namespace ThinkTech.Storage.Api.Storage;

/// <summary>
/// Stores documents on the local file system:
/// <c>{RootPath}/{clientId}/{id[0..2]}/{id[2..4]}/{id}.bin</c> plus <c>{id}.json</c> metadata.
/// File names on disk are server-generated ids, never user input, so path traversal is impossible.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private const string DataExtension = ".bin";
    private const string MetadataExtension = ".json";
    private const string TempExtension = ".tmp";
    private const int CopyBufferSize = 81920;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly TimeProvider _timeProvider;

    public LocalFileStorage(IOptions<StorageOptions> options, IHostEnvironment environment, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        RootPath = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public async Task<StoredFileMetadata> SaveAsync(
        string clientId, Stream content, FileUploadInfo upload, CancellationToken cancellationToken)
    {
        var fileId = Guid.NewGuid().ToString("N");
        var paths = GetPaths(clientId, fileId);
        var tempDataPath = paths.Data + TempExtension;

        Directory.CreateDirectory(paths.Directory);

        try
        {
            long size;
            string sha256;

            await using (var target = new FileStream(tempDataPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 1, FileOptions.Asynchronous))
            {
                (size, sha256) = await CopyAndHashAsync(content, target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            var metadata = new StoredFileMetadata
            {
                Id = fileId,
                ClientId = clientId,
                FileName = upload.FileName,
                ContentType = upload.ContentType,
                SizeBytes = size,
                Sha256 = sha256,
                Category = upload.Category,
                ReferenceId = upload.ReferenceId,
                Metadata = new Dictionary<string, string>(upload.Metadata, StringComparer.Ordinal),
                UploadedAtUtc = _timeProvider.GetUtcNow(),
            };

            // The data file is moved into place first; a file only "exists" once its metadata is written.
            File.Move(tempDataPath, paths.Data);
            await WriteMetadataAsync(paths.Metadata, metadata, cancellationToken);

            return metadata;
        }
        catch
        {
            TryDelete(tempDataPath);
            TryDelete(paths.Metadata + TempExtension);
            TryDelete(paths.Metadata);
            TryDelete(paths.Data);
            throw;
        }
    }

    public async Task<StoredFileMetadata?> GetMetadataAsync(string clientId, string fileId, CancellationToken cancellationToken)
    {
        var paths = GetPaths(clientId, fileId);

        try
        {
            await using var stream = new FileStream(paths.Metadata, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 4096, FileOptions.Asynchronous);
            var metadata = await JsonSerializer.DeserializeAsync<StoredFileMetadata>(stream, JsonOptions, cancellationToken);

            return metadata is not null && string.Equals(metadata.ClientId, clientId, StringComparison.Ordinal)
                ? metadata
                : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<StoredFileContent?> OpenReadAsync(string clientId, string fileId, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(clientId, fileId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        try
        {
            var stream = new FileStream(GetPaths(clientId, fileId).Data, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new StoredFileContent(metadata, stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public Task<bool> DeleteAsync(string clientId, string fileId, CancellationToken cancellationToken)
    {
        var paths = GetPaths(clientId, fileId);
        if (!File.Exists(paths.Metadata))
        {
            return Task.FromResult(false);
        }

        // Metadata first, so a half-finished delete never leaves a visible file without content.
        File.Delete(paths.Metadata);
        File.Delete(paths.Data);
        return Task.FromResult(true);
    }

    private FilePaths GetPaths(string clientId, string fileId)
    {
        if (!StorageIdentifiers.ClientId().IsMatch(clientId))
        {
            throw new ArgumentException("Invalid client id.", nameof(clientId));
        }

        if (!StorageIdentifiers.FileId().IsMatch(fileId))
        {
            throw new ArgumentException("Invalid file id.", nameof(fileId));
        }

        // Two levels of 256 shard folders keep directory sizes small even with millions of files.
        var directory = Path.Combine(RootPath, clientId, fileId[..2], fileId[2..4]);
        return new FilePaths(
            directory,
            Path.Combine(directory, fileId + DataExtension),
            Path.Combine(directory, fileId + MetadataExtension));
    }

    private static async Task<(long Size, string Sha256)> CopyAndHashAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }

            return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteMetadataAsync(string path, StoredFileMetadata metadata, CancellationToken cancellationToken)
    {
        var tempPath = path + TempExtension;

        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, bufferSize: 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a failed upload.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct FilePaths(string Directory, string Data, string Metadata);
}
