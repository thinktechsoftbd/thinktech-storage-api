using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ThinkTech.Storage.Api.Configuration;

namespace ThinkTech.Storage.Api.Storage;

/// <summary>
/// Stores documents on the local file system:
/// <list type="bullet">
///   <item>content: <c>{RootPath}/{clientId}/{filePartialPath}/{id}.{ext}</c>, e.g. <c>bizone-erp/invoices/2026/11/{id}.jpg</c></item>
///   <item>metadata: <c>{RootPath}/{clientId}/.metadata/{id[0..2]}/{id[2..4]}/{id}.json</c>. It records where the
///   content is, so a file is found by its id alone.</item>
/// </list>
/// The partial path only accepts safe folder names (no "..", no hidden folders) and the file name is a server-generated
/// id plus a validated extension, so nothing can be written outside the client's own folder.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private const string MetadataFolder = ".metadata";
    private const string FallbackDataExtension = ".bin";
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
        var metadataPath = GetMetadataPath(clientId, fileId);
        var dataFileName = GetDataFileName(fileId, upload.FileName);
        var dataPath = GetDataPath(clientId, upload.FilePartialPath, dataFileName);
        var tempDataPath = dataPath + TempExtension;

        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);

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
                StoredFileName = dataFileName,
                FilePartialPath = upload.FilePartialPath.Length == 0 ? null : upload.FilePartialPath,
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
            File.Move(tempDataPath, dataPath);
            await WriteMetadataAsync(metadataPath, metadata, cancellationToken);

            return metadata;
        }
        catch
        {
            TryDelete(tempDataPath);
            TryDelete(metadataPath + TempExtension);
            TryDelete(metadataPath);
            TryDelete(dataPath);
            throw;
        }
    }

    public async Task<StoredFileMetadata?> GetMetadataAsync(string clientId, string fileId, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(clientId, fileId);

        try
        {
            await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read,
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
            var dataPath = GetDataPath(clientId, metadata.FilePartialPath, metadata.StoredFileName);
            var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new StoredFileContent(metadata, stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string clientId, string fileId, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(clientId, fileId, cancellationToken);
        if (metadata is null)
        {
            return false;
        }

        // Metadata first, so a half-finished delete never leaves a visible file without content.
        File.Delete(GetMetadataPath(clientId, fileId));
        File.Delete(GetDataPath(clientId, metadata.FilePartialPath, metadata.StoredFileName));
        return true;
    }

    private string GetClientRoot(string clientId)
    {
        if (!StorageIdentifiers.ClientId().IsMatch(clientId))
        {
            throw new ArgumentException("Invalid client id.", nameof(clientId));
        }

        return Path.Combine(RootPath, clientId);
    }

    /// <summary>Two levels of 256 shard folders keep the metadata index small even with millions of files.</summary>
    private string GetMetadataPath(string clientId, string fileId)
    {
        if (!StorageIdentifiers.FileId().IsMatch(fileId))
        {
            throw new ArgumentException("Invalid file id.", nameof(fileId));
        }

        return Path.Combine(GetClientRoot(clientId), MetadataFolder, fileId[..2], fileId[2..4], fileId + MetadataExtension);
    }

    /// <summary><c>{RootPath}/{clientId}/{filePartialPath}/{dataFileName}</c>, guaranteed to stay inside the client's folder.</summary>
    private string GetDataPath(string clientId, string? filePartialPath, string dataFileName)
    {
        if (!StorageIdentifiers.DataFileName().IsMatch(dataFileName))
        {
            throw new InvalidDataException($"Invalid stored file name '{dataFileName}'.");
        }

        if (!FilePartialPathParser.TryNormalize(filePartialPath, out var normalized, out _) ||
            !string.Equals(normalized, filePartialPath ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid file partial path '{filePartialPath}'.");
        }

        var clientRoot = GetClientRoot(clientId);
        var parts = new List<string> { clientRoot };
        if (normalized.Length > 0)
        {
            parts.AddRange(normalized.Split('/'));
        }

        parts.Add(dataFileName);
        var fullPath = Path.GetFullPath(Path.Combine(parts.ToArray()));

        // Defense in depth: the validation above already rules this out.
        if (!fullPath.StartsWith(clientRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file path resolves outside the client's folder.");
        }

        return fullPath;
    }

    /// <summary>
    /// The data file keeps the uploaded file's extension (lowercased): <c>{id}.jpg</c>, <c>{id}.pdf</c>, ...
    /// A missing or unusual extension (not short and alphanumeric) falls back to <c>{id}.bin</c>.
    /// </summary>
    private static string GetDataFileName(string fileId, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return fileId + (StorageIdentifiers.DataExtension().IsMatch(extension) ? extension : FallbackDataExtension);
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
}
