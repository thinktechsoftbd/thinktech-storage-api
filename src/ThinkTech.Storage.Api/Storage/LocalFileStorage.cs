using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ThinkTech.Storage.Api.Configuration;

namespace ThinkTech.Storage.Api.Storage;

/// <summary>
/// Stores only the uploaded file, at <c>{RootPath}/{clientId}/{filePartialPath}/{id}.{ext}</c>,
/// e.g. <c>bizone-erp/invoices/2026/11/{id}.jpg</c>. There is no metadata file: the caller keeps the returned
/// file path and sends it back to download or delete.
/// The partial path only accepts safe folder names (no "..", no hidden folders) and the file name is a server-generated
/// id plus a validated extension, so nothing can be read or written outside the client's own folder.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private const string FallbackExtension = ".bin";
    private const string TempExtension = ".tmp";
    private const int CopyBufferSize = 81920;

    private readonly TimeProvider _timeProvider;

    public LocalFileStorage(IOptions<StorageOptions> options, IHostEnvironment environment, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        RootPath = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public async Task<StoredFile> SaveAsync(
        string clientId, Stream content, string filePartialPath, string extension, CancellationToken cancellationToken)
    {
        var fileId = Guid.NewGuid().ToString("N");
        var fileName = fileId + (StorageIdentifiers.DataExtension().IsMatch(extension) ? extension : FallbackExtension);
        var fullPath = GetFullPath(clientId, filePartialPath, fileName);
        var tempPath = fullPath + TempExtension;

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        try
        {
            long size;
            string sha256;

            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 1, FileOptions.Asynchronous))
            {
                (size, sha256) = await CopyAndHashAsync(content, target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            // Written to a temp name first, so a half-uploaded file is never visible under its real name.
            File.Move(tempPath, fullPath);

            return new StoredFile(fileId, CombineFilePath(filePartialPath, fileName), fileName, size, sha256, _timeProvider.GetUtcNow());
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public Task<StoredFileContent?> OpenReadAsync(string clientId, string filePath, CancellationToken cancellationToken)
    {
        if (!StoredFilePathParser.TryParse(filePath, out var partialPath, out var fileName))
        {
            return Task.FromResult<StoredFileContent?>(null);
        }

        var fullPath = GetFullPath(clientId, partialPath, fileName);

        try
        {
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath));

            return Task.FromResult<StoredFileContent?>(
                new StoredFileContent(CombineFilePath(partialPath, fileName), fileName, stream.Length, lastModified, stream));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<StoredFileContent?>(null);
        }
    }

    public Task<bool> DeleteAsync(string clientId, string filePath, CancellationToken cancellationToken)
    {
        if (!StoredFilePathParser.TryParse(filePath, out var partialPath, out var fileName))
        {
            return Task.FromResult(false);
        }

        var fullPath = GetFullPath(clientId, partialPath, fileName);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(false);
        }

        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    /// <summary><c>{RootPath}/{clientId}/{filePartialPath}/{fileName}</c>, guaranteed to stay inside the client's folder.</summary>
    private string GetFullPath(string clientId, string filePartialPath, string fileName)
    {
        if (!StorageIdentifiers.ClientId().IsMatch(clientId))
        {
            throw new ArgumentException("Invalid client id.", nameof(clientId));
        }

        if (!StorageIdentifiers.DataFileName().IsMatch(fileName))
        {
            throw new ArgumentException("Invalid file name.", nameof(fileName));
        }

        if (!FilePartialPathParser.TryNormalize(filePartialPath, out var normalized, out _) ||
            !string.Equals(normalized, filePartialPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid file partial path.", nameof(filePartialPath));
        }

        var clientRoot = Path.Combine(RootPath, clientId);
        var parts = new List<string> { clientRoot };
        if (normalized.Length > 0)
        {
            parts.AddRange(normalized.Split('/'));
        }

        parts.Add(fileName);
        var fullPath = Path.GetFullPath(Path.Combine(parts.ToArray()));

        // Defense in depth: the validation above already rules this out.
        if (!fullPath.StartsWith(clientRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The file path resolves outside the client's folder.", nameof(filePartialPath));
        }

        return fullPath;
    }

    private static string CombineFilePath(string filePartialPath, string fileName) =>
        filePartialPath.Length == 0 ? fileName : $"{filePartialPath}/{fileName}";

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
